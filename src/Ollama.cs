using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;

namespace LlamaLauncher;
// Ollama's API on its own port for apps that only accept Ollama: ollama/ollama_shim.py beside the app, a standard-library
// Python server that forwards to this launcher's server. The shim reads the port and key from settings.json itself, so it
// follows model, port and key changes without a restart, and the launcher's own port and API are unchanged. It runs
// outside the launch job, so it keeps answering while the window is closed; the HKCU Run entry starts it at sign-in while
// it is on. Tailscale sharing is `tailscale serve` TCP forwarding of the same port on the tailnet addresses only, done
// by tailscaled, so no firewall rule is involved. Like Ollama it has no API key.
public static class Ollama
{
    public const int Port = 11434;
    public static string LocalUrl => $"http://127.0.0.1:{Port}";
    public enum Status { Off, Running, PortTaken }
    public sealed record Tailnet(string Ip, string Name, string? Problem)
    {
        public string Url => $"http://{Ip}:{Port}";
    }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "llama.cpp Launcher Ollama API";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    public static string Script => Path.Combine(AppContext.BaseDirectory, "ollama", "ollama_shim.py");
    public static string LogFile(string dataRoot) => Path.Combine(dataRoot, "logs", "ollama-shim.log");
    // The optional client key; the shim reads OllamaRequireKey and OllamaApiKey from settings.json on its next request.
    public static string NewKey() => "ollama-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    static string? OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(d => !d.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) // Store aliases open the Store
        .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    public static string? Python(Settings s)
    {
        string venv = s.PolyStrataRoot.Length > 0 ? Path.Combine(s.PolyStrataRoot, ".venv", "Scripts", "pythonw.exe") : "";
        return OnPath("pythonw.exe") ?? (File.Exists(venv) ? venv : null);
    }
    static string RequirePython(Settings s) => Python(s) ?? throw new FileNotFoundException(
        "The Ollama API needs Python 3.9 or later (pythonw.exe on PATH). Install Python from python.org, then turn it on again.");
    static List<string> Arguments(string dataRoot) => new() { "-I", Script, "--settings", Path.Combine(dataRoot, "settings.json"), "--log", LogFile(dataRoot) };

    public static async Task<Status> Probe()
    {
        // The listener table, not a connect: Windows retries a refused local connect for about two seconds. Tailscale's
        // own listener on the tailnet addresses (sharing) is not this port being taken.
        if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == Port
                && (IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any))))
            return Status.Off;
        try
        {
            using var r = await http.GetAsync(LocalUrl + "/");
            return r.Headers.Server.ToString().StartsWith("ollama-shim", StringComparison.OrdinalIgnoreCase) ? Status.Running : Status.PortTaken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Status.PortTaken; }
    }
    public static async Task<Status> Start(Settings s, string dataRoot)
    {
        string python = RequirePython(s);
        if (!File.Exists(Script))
            throw new FileNotFoundException("This installation is missing the Ollama API script: " + Script);
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Script)! };
        foreach (var a in Arguments(dataRoot))
            start.ArgumentList.Add(a);
        Process.Start(start)?.Dispose();
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(200);
            var state = await Probe();
            if (state != Status.Off)
                return state;
        }
        throw new TimeoutException("The Ollama API did not start. Its log is " + LogFile(dataRoot));
    }
    // Every copy of the shim, also one started by hand, so the port is free afterwards. Ollama itself is not touched.
    public static void Stop()
    {
        using var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'python%'");
        using var found = search.Get();
        foreach (ManagementObject p in found)
            using (p)
                if (((string?)p["CommandLine"])?.Contains("ollama_shim.py", StringComparison.OrdinalIgnoreCase) == true)
                    try
                    {
                        using var process = Process.GetProcessById(Convert.ToInt32(p["ProcessId"]));
                        process.Kill();
                    }
                    catch (Exception) { } // exited meanwhile
    }
    public static void AutoStart(Settings s, string dataRoot, bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            key.SetValue(RunValue, string.Join(" ", Arguments(dataRoot).Prepend(RequirePython(s)).Select(a => "\"" + a + "\"")));
        else
            key.DeleteValue(RunValue, false);
    }

    public static string? Tailscale()
    {
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(installed) ? installed : OnPath("tailscale.exe");
    }
    internal static async Task<(int Code, string Output, string Error)> Tool(string exe, string arguments)
    {
        using var p = new Process { StartInfo = new(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        p.Start();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var c = new CancellationTokenSource(15000);
        try
        {
            await p.WaitForExitAsync(c.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            throw new TimeoutException("Tailscale did not answer. Check that it is running and signed in.");
        }
        return (p.ExitCode, await output, await error);
    }
    static async Task<bool> SharedOnTailnet(string exe)
    {
        var (code, output, _) = await Tool(exe, "serve status --json");
        if (code != 0)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.TryGetProperty("TCP", out var tcp)
                && tcp.TryGetProperty(Port.ToString(), out var entry) && entry.TryGetProperty("TCPForward", out var to)
                && to.GetString() == $"127.0.0.1:{Port}";
        }
        catch (JsonException) { return false; }
    }
    public static async Task<Tailnet> TailnetAddress()
    {
        if (Tailscale() is not string exe)
            return new("", "", "Tailscale is not installed.");
        var (code, output, error) = await Tool(exe, "status --json");
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            string state = root.TryGetProperty("BackendState", out var b) ? b.GetString() ?? "" : "";
            if (state != "Running")
                return new("", "", $"Tailscale is not connected ({(state.Length > 0 ? state : "not running")}).");
            var self = root.GetProperty("Self");
            string ip = self.GetProperty("TailscaleIPs").EnumerateArray().Select(x => x.GetString() ?? "").FirstOrDefault(x => x.Contains('.')) ?? "";
            return new(ip, (self.TryGetProperty("DNSName", out var n) ? n.GetString() ?? "" : "").TrimEnd('.'), ip.Length > 0 ? null : "Tailscale has no IPv4 address yet.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new("", "", "Tailscale did not report its address. " + (code != 0 ? error.Trim() : ""));
        }
    }

    // On: running, starting at sign-in, and shared on the tailnet exactly when `tailnet` is. Off: none of these.
    public static async Task Apply(Settings s, string dataRoot, bool on, bool tailnet)
    {
        if (on)
        {
            var state = await Probe();
            if (state == Status.PortTaken)
                throw new InvalidOperationException($"Port {Port} is used by another program (Ollama itself?). Quit it, then turn the Ollama API on again.");
            if (state == Status.Off)
                await Start(s, dataRoot);
            AutoStart(s, dataRoot, true);
        }
        else
        {
            AutoStart(s, dataRoot, false);
            await Task.Run(Stop);
        }
        bool share = on && tailnet;
        string? exe = Tailscale();
        if (exe == null)
        {
            if (share)
                throw new FileNotFoundException("Tailscale is not installed. Install it from tailscale.com and sign in, then turn sharing on again.");
            return;
        }
        if (await SharedOnTailnet(exe) == share)
            return;
        var (code, output, error) = await Tool(exe, share ? $"serve --bg --tcp {Port} tcp://127.0.0.1:{Port}" : $"serve --tcp={Port} off");
        if (code != 0)
            throw new InvalidOperationException($"Tailscale could not {(share ? "share" : "stop sharing")} the Ollama API: {(error + " " + output).Trim()}");
    }
}
