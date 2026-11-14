using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LlamaLauncher;
public static class Integration
{
    public static string Fingerprint(Settings s, Profile p)
    {
        var keys = new[] { "ContextSize", "Thinking", "PreserveThinking", "ReasoningEffort", "Temperature", "TopP", "TopK", "MinP", "PresencePenalty", "RepeatPenalty", "MaxOutputTokens", "Mmproj" };
        var text = string.Join("|", new[] { p.Id, p.Path, p.Alias, p.RuntimeId, p.Acceleration, p.Ngram, p.DraftModel, p.TurboQuantEnabled.ToString(), p.TurboQuantPreset, s.Port.ToString(), s.ApiKey }) + "|" + string.Join("|", keys.Select(k => p.Options.S(k)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    public static void DesktopShortcut()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "llama.cpp Launcher.lnk"));
        link.TargetPath = Environment.ProcessPath!;
        link.WorkingDirectory = AppContext.BaseDirectory;
        link.Description = "Run local GGUF models";
        link.Save();
    }
    public static string LanAddress() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).SelectMany(n => n.GetIPProperties().UnicastAddresses).FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))?.Address.ToString() ?? "127.0.0.1";
    public static async Task Firewall(string engine, int port)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--firewall");
        start.ArgumentList.Add(engine);
        start.ArgumentList.Add(port.ToString());
        using var p = Process.Start(start)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("Windows Firewall could not be updated. Check administrator approval and policy.");
    }
    public static void ConfigureFirewall(string engine, int port)
    {
        if (port < 1024 || port > 65535 || !File.Exists(engine) || Path.GetFileName(engine) != "llama-server.exe")
            throw new ArgumentException("Invalid firewall parameters");
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        string name = "llama.cpp Launcher (LocalSubnet)";
        try
        {
            policy.Rules.Remove(name);
        }
        catch { }
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        rule.Name = name;
        rule.Description = "Launcher-owned llama.cpp server, local subnet only";
        rule.ApplicationName = Path.GetFullPath(engine);
        rule.Protocol = 6;
        rule.LocalPorts = port.ToString();
        rule.RemoteAddresses = "LocalSubnet";
        rule.Direction = 1;
        rule.Action = 1;
        rule.Enabled = true;
        rule.Profiles = 7;
        rule.EdgeTraversal = false;
        policy.Rules.Add(rule);
    }
    public static System.Text.Json.Nodes.JsonObject QwenConfiguration(Settings s, Profile p, string engine, string? liveModelId = null, int? liveContext = null)
    {
        string modelId = string.IsNullOrWhiteSpace(liveModelId) ? p.Alias : liveModelId;
        int context = liveContext is > 0 ? liveContext.Value : p.Options.I("ContextSize");
        var cfg = Profile.Defaults();
        foreach (var entry in p.Options)
            if (entry.Value is not null)
                cfg[entry.Key] = entry.Value.DeepClone();
        cfg["Model"] = p.Path;
        cfg["ModelAlias"] = modelId;
        cfg["ContextSize"] = context;
        cfg["ModelDisplayName"] = p.Name;
        cfg["LlamaServer"] = engine;
        cfg["Port"] = s.Port;
        cfg["Host"] = s.Lan ? "0.0.0.0" : "127.0.0.1";
        cfg["LanAccess"] = s.Lan;
        cfg["ApiKey"] = s.ApiKey;
        cfg["QwenSettings"] = s.LegacySettings;
        cfg["QwenDesktop"] = s.LegacyDesktop;
        cfg["Mmproj"] = p.Options.S("Mmproj");
        cfg["ChatTemplate"] = p.Options.S("ChatTemplate");
        cfg["SamplingProfile"] = p.Options.S("SamplingProfile", "custom");
        cfg["MtpEnabled"] = p.Acceleration != "Off" && (File.Exists(p.DraftModel) || p.Options.B("MtpEnabled"));
        cfg["ActiveModelId"] = p.Id;
        var model = p.Options.Clone();
        model["Id"] = p.Id;
        model["Model"] = p.Path;
        model["ModelAlias"] = modelId;
        model["ContextSize"] = context;
        model["DisplayName"] = p.Name;
        model["NativeContextLimit"] = Math.Max(context, p.Options.I("NativeContextLimit", 262144));
        cfg["Models"] = new System.Text.Json.Nodes.JsonArray(model);
        // These legacy validator fields are not consumed by Qwen Code synchronization.
        // Keep the bridge valid without limiting the actual engine's advanced controls.
        cfg["BatchSize"] = 1024;
        cfg["UBatchSize"] = 512;
        cfg["Threads"] = Math.Clamp(p.Options.I("Threads"), 1, 16);
        cfg["MtpDraftMax"] = Math.Clamp(p.Options.I("MtpDraftMax", 2), 1, 8);
        cfg["MaxOutputTokens"] = Math.Clamp(p.Options.I("MaxOutputTokens", 2048), 128, context);
        if (!p.Options.B("Thinking"))
            cfg["PreserveThinking"] = false;
        return cfg;
    }
    public static bool FirewallMatches(string engine, int port)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            dynamic rule = policy.Rules.Item("llama.cpp Launcher (LocalSubnet)");
            return rule.Enabled && string.Equals((string)rule.ApplicationName, Path.GetFullPath(engine), StringComparison.OrdinalIgnoreCase)
                && (string)rule.LocalPorts == port.ToString() && (string)rule.RemoteAddresses == "LocalSubnet" && rule.Action == 1 && rule.Direction == 1;
        }
        catch { return false; }
    }
    // Qwen Code Desktop reads its model list once, when it opens (its `serve` agent keeps it), so a window that was
    // already open before a sync keeps showing the previous model until it is reopened.
    public static bool DesktopOpenedBefore(string desktopPath, DateTime moment)
    {
        if (string.IsNullOrWhiteSpace(desktopPath))
            return false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(desktopPath)))
            using (process)
                try
                {
                    if (process.StartTime < moment)
                        return true;
                }
                catch (Exception) { } // exited meanwhile, or not ours to inspect
        return false;
    }
    public static async Task SyncQwen(ConfigStore store, Profile p, string engine, string? liveModelId = null, int? liveContext = null)
    {
        var s = store.Value;
        if (!File.Exists(s.LegacyCommon) || !File.Exists(s.LegacySettings))
            throw new InvalidOperationException("Qwen synchronization requires the imported installation's compatibility script and Qwen settings path.");
        var cfg = QwenConfiguration(s, p, engine, liveModelId, liveContext);
        string path = Path.Combine(store.Root, "qwen-sync.tmp.json");
        File.WriteAllText(path, cfg.ToJsonString());
        string Quote(string v) => "'" + v.Replace("'", "''") + "'";
        // Call the existing integration exactly, including its established compatibility repairs.
        string command = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; try { . " + Quote(s.LegacyCommon) + "; $c=Get-Content -LiteralPath " + Quote(path) + " -Raw | ConvertFrom-Json; Sync-QwenCodeSettings -Config $c } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        try
        {
            await EngineManager.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)), 60000);
            s.QwenSyncSignature = Fingerprint(s, p);
            store.Save();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Qwen Code settings synchronization failed: " + ex.Message.Replace("Engine probe failed", "Compatibility script failed"), ex);
        }
        finally { File.Delete(path); }
    }
}
