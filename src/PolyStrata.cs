using System.Diagnostics;
using System.Management;
using System.Text.Json.Nodes;

namespace LlamaLauncher;
// Experimental alternative engine: PolyStrata (github.com/VecSzn/PolyStrata) serves the MoE architectures it
// implements through its own Python server and engine child process, with the same OpenAI API on the launcher's
// port. Its setup writes a run config (strata-<model>.json) per installed model; the launcher only starts, checks
// and stops that server. Dense models and NVFP4 files stay on llama.cpp.
public static class PolyStrata
{
    public const string Runtime = "PolyStrata";
    public const string LegacyRuntime = "PolyStrata (Experimental)";
    // Architectures its engines load (docs/BACKENDS.md); the server refuses any other GGUF by name.
    public static readonly string[] Architectures =
        { "qwen4exp", "glm5-next", "qwen35moe", "gemma4", "deepseek4", "lfm2moe", "flint", "kolibri1", "clover_moe" };
    public static bool Supports(ModelInfo m) =>
        Architectures.Contains(m.Architecture, StringComparer.OrdinalIgnoreCase);
    public static string Python(string root) => Path.Combine(root, ".venv", "Scripts", "python.exe");
    public static string Server(string root) => Path.Combine(root, "serve", "server.py");
    // The vision encoder's directory (server.py work_dir(): strata-vision-* in the temp directory, else the server's
    // working directory or the system drive's root) holds the encoded pictures of requests, and PolyStrata never
    // removes it, not even when it stops. Remove those of servers that have ended: one created after a running
    // PolyStrata server started may be that server's and is kept. A directory some process still works in cannot be
    // moved, so it is never half deleted. Returns how many are left (kept for a live server, or still in use).
    public static int CleanVisionCache(string root)
    {
        var oldestLive = DateTime.MaxValue;
        using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'python%'"))
        using (var found = search.Get())
            foreach (ManagementObject p in found)
                using (p)
                    if ((string?)p["CommandLine"] is string line && line.Contains("server.py", StringComparison.OrdinalIgnoreCase) && line.Contains("strata", StringComparison.OrdinalIgnoreCase))
                        try
                        {
                            using var process = Process.GetProcessById(Convert.ToInt32(p["ProcessId"]));
                            if (process.StartTime < oldestLive)
                                oldestLive = process.StartTime;
                        }
                        catch (Exception) { } // exited meanwhile
        var parents = new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("TEMP") ?? "", root, Path.GetPathRoot(Environment.SystemDirectory) ?? "" };
        int left = 0;
        foreach (string parent in parents.Where(d => d.Length > 0 && Directory.Exists(d)).Select(d => Path.GetFullPath(d).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var dir in new DirectoryInfo(parent).EnumerateDirectories("strata-vision-*").ToList())
            {
                left++;
                if (dir.CreationTime >= oldestLive.AddSeconds(-5))
                    continue;
                string doomed = dir.FullName + ".removing";
                try
                {
                    if (!dir.Name.EndsWith(".removing", StringComparison.OrdinalIgnoreCase))
                        dir.MoveTo(doomed);
                    else
                        doomed = dir.FullName;
                    Directory.Delete(doomed, true);
                    left--;
                }
                catch (Exception) { } // in use: the next Stop or start tries again
            }
        return left;
    }
    public static string? InstallProblem(string root) =>
        string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? "Choose the PolyStrata folder under Advanced."
        : !File.Exists(Server(root)) ? "The selected folder is not a PolyStrata checkout (serve\\server.py is missing)."
        : !File.Exists(Python(root)) ? "PolyStrata's Python environment is missing. Run its setup once (START-HERE.bat or python setup.py)."
        : null;
    // Run configs are strata-<model>.json with an "exe" and "args"; strata-*.shared-settings.json are not configs.
    public static IEnumerable<string> Configs(string root)
    {
        if (!Directory.Exists(root))
            yield break;
        foreach (var file in Directory.EnumerateFiles(root, "strata-*.json"))
        {
            if (file.EndsWith(".shared-settings.json", StringComparison.OrdinalIgnoreCase))
                continue;
            JsonObject? o = null;
            try { o = JsonNode.Parse(File.ReadAllText(file))?.AsObject(); } catch { }
            if (o?["exe"] != null && o["args"] is JsonArray)
                yield return file;
        }
    }
    // The config whose engine arguments name this GGUF (any shard) or its folder; with a single config, that one.
    public static string? MatchConfig(string root, string ggufPath)
    {
        var configs = Configs(root).ToList();
        string file = Path.GetFileName(ggufPath);
        string stem = System.Text.RegularExpressions.Regex.Replace(file, @"-\d{5}-of-\d{5}\.gguf$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string folder = Path.GetDirectoryName(Path.GetFullPath(ggufPath)) ?? "";
        foreach (var config in configs)
        {
            string text = File.ReadAllText(config).Replace("\\\\", "\\").Replace('/', '\\');
            if (text.Contains(stem, StringComparison.OrdinalIgnoreCase)
                || (folder.Length > 3 && text.Contains(folder, StringComparison.OrdinalIgnoreCase)))
                return config;
        }
        return configs.Count == 1 ? configs[0] : null;
    }
    // The launcher identifies its server by the profile alias. PolyStrata answers to the names in the config's
    // "aliases" list, so the alias is added there (kept alongside any existing names; a .bak is kept, as PolyStrata does).
    public static void EnsureAlias(string config, string alias)
    {
        var o = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        var names = o["aliases"] switch
        {
            JsonArray a => a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList(),
            JsonValue v when v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) => s.Split(',').Select(x => x.Trim()).ToList(),
            _ => new List<string>()
        };
        if (names.Contains(alias))
            return;
        names.Add(alias);
        o["aliases"] = new JsonArray(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        File.Copy(config, config + ".bak", true);
        File.WriteAllText(config, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    // PolyStrata's KV cache types are int8 (8-bit K and V), k8v4 (8-bit K, 4-bit V) and q4_0 (4-bit both); there is
    // no 16-bit cache and no 4-bit-K/8-bit-V mix. The launcher's choice maps to the nearest that keeps at least its
    // precision; the note says when that differs from what was selected.
    public static (string Kv, string Note) KvFor(string cacheK, string cacheV)
    {
        static bool Eight(string t) => !t.StartsWith("Q4", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("IQ4", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("Q5", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("Turbo", StringComparison.OrdinalIgnoreCase);
        bool k8 = Eight(cacheK), v8 = Eight(cacheV);
        string kv = k8 && !v8 ? "k8v4" : !k8 && !v8 ? "q4_0" : "int8";
        bool exact = (kv == "int8" && cacheK.Equals("Q8_0", StringComparison.OrdinalIgnoreCase) && cacheV.Equals("Q8_0", StringComparison.OrdinalIgnoreCase))
            || (kv == "k8v4" && cacheK.Equals("Q8_0", StringComparison.OrdinalIgnoreCase) && cacheV.Equals("Q4_0", StringComparison.OrdinalIgnoreCase))
            || (kv == "q4_0" && cacheK.Equals("Q4_0", StringComparison.OrdinalIgnoreCase) && cacheV.Equals("Q4_0", StringComparison.OrdinalIgnoreCase));
        return (kv, exact ? "" : $"PolyStrata has no {cacheK}/{cacheV} KV cache; it uses {kv} (its nearest at or above that precision).");
    }
    // Context and KV cache live in the run config's engine arguments (--max-context, --kv above 8K), so the
    // launcher's controls are written there before each start. Other engine arguments are left as setup wrote them.
    public static int ReadParallel(string config)
    {
        try { return Math.Clamp(JsonNode.Parse(File.ReadAllText(config))?["parallel"]?.GetValue<int>() ?? 1, 1, 8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { return 1; }
    }

    public static string ApplyLaunchSettings(string config, int context, string cacheK, string cacheV, int? parallel = null)
    {
        var o = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        var args = (o["args"] as JsonArray ?? throw new InvalidDataException("The PolyStrata run config has no engine arguments.")).Select(x => x?.ToString() ?? "").ToList();
        void Set(string flag, string? value)
        {
            int i = args.IndexOf(flag);
            if (value == null) { if (i >= 0) args.RemoveRange(i, Math.Min(2, args.Count - i)); return; }
            if (i >= 0 && i + 1 < args.Count) args[i + 1] = value;
            else args.AddRange(new[] { flag, value });
        }
        var (kv, note) = KvFor(cacheK, cacheV);
        Set("--max-context", context.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("--kv", context > 8192 ? kv : null);
        var updated = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        if (parallel is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(parallel), "PolyStrata supports 1–8 slots here.");
        bool parallelChanged = parallel.HasValue && (o["parallel"]?.GetValue<int>() ?? 1) != parallel.Value;
        if (updated.ToJsonString() != o["args"]!.ToJsonString() || parallelChanged)
        {
            o["args"] = updated;
            if (parallel.HasValue) o["parallel"] = parallel.Value;
            File.Copy(config, config + ".bak", true);
            File.WriteAllText(config, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        return note;
    }

    // ---- updates: tagged GitHub releases, like the llama.cpp engine. The checkout moves to the release tag, then
    // PolyStrata's own updater refreshes its engine, Python packages and model settings (setup.py --update). The
    // previous tag is remembered so Roll back can return to it (setup.py --rollback-engine restores the engine).
    public const string Repository = "VecSzn/PolyStrata";
    static string StatePath(string root) => Path.Combine(root, ".launcher-update.json");
    public static async Task<string> InstalledVersion(string root)
    {
        try { return (await Run("git", $"-C \"{root}\" describe --tags --exact-match", root, TimeSpan.FromSeconds(20))).Trim(); }
        catch { }
        try { return "commit " + (await Run("git", $"-C \"{root}\" rev-parse --short HEAD", root, TimeSpan.FromSeconds(20))).Trim(); }
        catch { return "unknown"; }
    }
    public static async Task<string> LatestRelease()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaLauncher");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v[\w.\-]+$"))
            throw new InvalidDataException("GitHub returned an unexpected PolyStrata release tag.");
        return tag;
    }
    public static async Task Update(string root, string tag, Action<string> log)
    {
        if (InstallProblem(root) is string problem)
            throw new InvalidOperationException(problem);
        string current = await InstalledVersion(root);
        log(await Run("git", $"-C \"{root}\" fetch --depth 1 --no-tags origin tag {tag}", root, TimeSpan.FromMinutes(10)));
        log(await Run("git", $"-C \"{root}\" checkout --detach {tag}", root, TimeSpan.FromMinutes(2)));
        File.WriteAllText(StatePath(root), new JsonObject { ["Current"] = tag, ["Previous"] = current }.ToJsonString());
        log(await Run(Python(root), "setup.py --update --yes --no-browser", root, TimeSpan.FromMinutes(45)));
    }
    public static async Task<string> Rollback(string root, Action<string> log)
    {
        var state = File.Exists(StatePath(root)) ? JsonNode.Parse(File.ReadAllText(StatePath(root)))?.AsObject() : null;
        string previous = state?["Previous"]?.ToString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(previous, @"^v[\w.\-]+$"))
            throw new InvalidOperationException("No earlier PolyStrata release is recorded to roll back to.");
        string current = await InstalledVersion(root);
        log(await Run("git", $"-C \"{root}\" fetch --depth 1 --no-tags origin tag {previous}", root, TimeSpan.FromMinutes(10)));
        log(await Run("git", $"-C \"{root}\" checkout --detach {previous}", root, TimeSpan.FromMinutes(2)));
        log(await Run(Python(root), "setup.py --rollback-engine --yes --no-browser", root, TimeSpan.FromMinutes(20)));
        File.WriteAllText(StatePath(root), new JsonObject { ["Current"] = previous, ["Previous"] = current }.ToJsonString());
        return previous;
    }
    static async Task<string> Run(string exe, string arguments, string workingDirectory, TimeSpan timeout)
    {
        using var p = new System.Diagnostics.Process { StartInfo = new(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory } };
        p.StartInfo.Environment["PYTHONUTF8"] = "1";
        p.Start();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cancel = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cancel.Token); }
        catch (OperationCanceledException) { if (!p.HasExited) p.Kill(true); throw new TimeoutException($"{Path.GetFileName(exe)} {arguments.Split(' ')[0]} timed out."); }
        string text = (await output) + (await error);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {p.ExitCode}): " + text[Math.Max(0, text.Length - 1500)..]);
        return text;
    }

    // PolyStrata binds to this PC only: a LAN listener would be python.exe, which the launcher's per-engine
    // firewall rule does not cover.
    public static List<string> Arguments(Settings s, Profile p) => new()
    {
        Server(s.PolyStrataRoot), "--engine", "strata", "--config", p.PolyStrataConfig,
        "--host", "127.0.0.1", "--port", s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--api-key", s.ApiKey
    };
}
