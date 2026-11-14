using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Management;

namespace LlamaLauncher;
public sealed class RuntimeManager : IDisposable
{
    Process? process; IntPtr job; readonly ConfigStore store; readonly EngineManager? engine; bool stopping;
    readonly Queue<string> logs = new(); readonly Queue<string> events = new(); readonly object gate = new();
    StreamWriter? persistentLog;
    CancellationTokenSource? tail; string outputFile = ""; long outputRead; System.Text.Decoder? outputDecoder;
    readonly System.Text.StringBuilder outputLine = new(); readonly object outputGate = new();
    string firstActionableCause = "";
    public string State { get; private set; } = "Stopped";
    public string Error { get; private set; } = "";
    public string LaunchSummary { get; private set; } = "";
    public event Action? Changed;
    public bool Active => process is { HasExited: false };
    public bool ExistingServer { get; private set; }
    public string LiveModelId { get; private set; } = "";
    public string LiveModelPath { get; private set; } = "";
    public int? LiveContext { get; private set; }
    public Profile? LiveProfile { get; private set; }
    public int? ProcessId => Active ? process!.Id : null;
    public RuntimeManager(ConfigStore store, EngineManager? engine = null)
    {
        this.store = store;
        this.engine = engine;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct TcpOwnerRow
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    internal static bool LocalServerAddress(IPAddress address) => IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    static bool LocalPortListening(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port && LocalServerAddress(x.Address));

    static int? PortOwner(int port)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (result != 122 || size < 4) return null;
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return null;
            uint count = (uint)Marshal.ReadInt32(table);
            int stride = Marshal.SizeOf<TcpOwnerRow>();
            int? wildcardOwner = null;
            for (uint i = 0; i < count && 4L + (long)(i + 1) * stride <= size; i++)
            {
                var row = Marshal.PtrToStructure<TcpOwnerRow>(IntPtr.Add(table, 4 + (int)i * stride));
                int rowPort = (int)((row.LocalPort & 0xff) << 8 | (row.LocalPort >> 8 & 0xff));
                if (rowPort != port) continue;
                var address = new IPAddress(row.LocalAddress);
                if (IPAddress.IsLoopback(address)) return (int)row.ProcessId;
                if (address.Equals(IPAddress.Any)) wildcardOwner = (int)row.ProcessId;
            }
            return wildcardOwner;
        }
        finally { Marshal.FreeHGlobal(table); }
    }
    static int? ParentOf(int pid)
    {
        try
        {
            using var query = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId={pid}");
            foreach (ManagementObject item in query.Get())
                return Convert.ToInt32(item["ParentProcessId"]);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException) { }
        return null;
    }
    // The process a reconnect will find on the port. llama-server listens itself; PolyStrata's venv python.exe is a
    // stub that runs the real interpreter as its child, and that child owns the port. Only a direct child counts.
    internal static int ServerProcessId(int launched, int? portOwner, int? ownerParent) =>
        portOwner is int owner && owner != launched && ownerParent == launched ? owner : launched;
    // The reconnect record: the process a reopened launcher will find on the port, with its start time so a reused PID
    // never matches. It stays current while a reconnected server runs, so a launcher that is ended abruptly (a crash,
    // Task Manager) leaves the next one able to reconnect and Stop instead of finding an unowned server.
    void Remember(Process server, Profile profile)
    {
        store.Value.DetachedServer = new()
        {
            ProcessId = server.Id,
            StartedUtcTicks = server.StartTime.ToUniversalTime().Ticks,
            Port = store.Value.Port,
            ProfileId = profile.Id,
            ModelId = LiveModelId,
            ModelPath = LiveModelPath,
            EnginePath = server.MainModule?.FileName ?? ""
        };
        store.Save();
    }
    void ClearDetached()
    {
        if (store.Value.DetachedServer == null) return;
        store.Value.DetachedServer = null;
        store.Save();
    }
    static bool HasArgument(string command, string flag, string value)
    {
        var matches = Regex.Matches(command, "\"[^\"]*\"|\\S+");
        for (int i = 0; i + 1 < matches.Count; i++)
            if (matches[i].Value.Equals(flag, StringComparison.OrdinalIgnoreCase)
                && matches[i + 1].Value.Trim('"').Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    bool IsLegacyLauncherProcess(Process candidate, Profile profile, string executable)
    {
        if (engine == null || LiveModelPath.Length == 0) return false;
        try
        {
            string root = Path.GetFullPath(engine.RootFor(profile)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(executable).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase)) return false;
            using var query = new ManagementObjectSearcher($"SELECT ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId={candidate.Id}");
            foreach (ManagementObject item in query.Get())
            {
                string path = item["ExecutablePath"] as string ?? "";
                string command = item["CommandLine"] as string ?? "";
                if (SameModelPath(path, executable) && HasArgument(command, "-m", profile.Path)
                    && HasArgument(command, "--alias", profile.Alias)
                    && HasArgument(command, "--port", store.Value.Port.ToString())) return true;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException) { }
        return false;
    }
    bool TryAttach(Profile profile)
    {
        // PolyStrata does not report a model path; its reconnect relies on the detached-server record below
        // (process, start time, port, profile and model ID), never on the legacy process heuristics.
        bool poly = profile.RuntimeId == PolyStrata.Runtime;
        bool basalt = profile.RuntimeId == Basalt.Runtime;
        if (basalt && LiveModelPath.Length > 0 && !SameModelPath(profile.BasaltPack, LiveModelPath)) return false;
        if (basalt) LiveModelPath = profile.Path;
        if (!poly && (LiveModelPath.Length == 0 || !SameModelPath(profile.Path, LiveModelPath))) return false;
        int? owner = PortOwner(store.Value.Port);
        if (owner == null || owner <= 0) return false;
        try
        {
            var candidate = Process.GetProcessById(owner.Value);
            string executable = candidate.MainModule?.FileName ?? "";
            var detached = store.Value.DetachedServer;
            bool marked = detached != null && detached.ProcessId == owner && detached.Port == store.Value.Port
                && detached.ProfileId == profile.Id && detached.ModelId == LiveModelId
                && SameModelPath(detached.ModelPath, profile.Path)
                && SameModelPath(detached.EnginePath, executable)
                && Math.Abs(candidate.StartTime.ToUniversalTime().Ticks - detached.StartedUtcTicks) < TimeSpan.TicksPerSecond;
            bool legacy = !poly && !basalt && detached == null && IsLegacyLauncherProcess(candidate, profile, executable);
            if (!marked && !legacy) { candidate.Dispose(); return false; }
            process = candidate;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => { if (!stopping) { ClearDetached(); Error = "Server exited. Open Logs for details."; SetState("Crashed"); } };
            if (poly) // PolyStrata's /props has no model path; it serves the GGUF its run config names
                LiveModelPath = profile.Path;
            Remember(candidate, profile);
            ExistingServer = false;
            Error = "";
            Log("Reconnected to the running launcher server on port " + store.Value.Port + ".");
            SetState("Running · Live");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return false; }
    }
    void ApplyWorkingSetLimit(Profile launch, ModelInfo model, bool report)
    {
        if (process == null || !model.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase) || !launch.Options.B("CpuMoe"))
            return;
        int workingSetGiB = Math.Clamp(launch.Options.I("WorkingSetLimitGiB", 65), 0, 1024);
        if (workingSetGiB == 0)
            return;
        ulong maximum = (ulong)workingSetGiB << 30;
        if (!SetProcessWorkingSetSizeEx(process.Handle, new UIntPtr(256UL << 20), new UIntPtr(maximum), 0x4))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to apply the configured server RAM working-set ceiling.");
        if (report)
            Log($"Windows resident working set capped at {workingSetGiB} GiB after model load; clean SSD-backed GGUF pages remain pageable.");
    }
    void SetState(string value)
    {
        State = value;
        lock (gate)
        {
            events.Enqueue(DateTime.UtcNow.ToString("O") + " " + value);
            while (events.Count > 40)
                events.Dequeue();
        }
        Changed?.Invoke();
    }
    public string Redact(string text)
    {
        if (store.Value.ApiKey.Length > 0)
            text = text.Replace(store.Value.ApiKey, "<redacted>");
        text = Regex.Replace(text, @"(?i)(Bearer\s+|api[-_ ]?key\s*[:=]\s*)\S+", "$1<redacted>");
        foreach (var p in store.Value.Models)
            foreach (var path in new[] { p.Path, p.BasaltPack, p.DraftModel, p.Options.S("Mmproj"), p.Options.S("ChatTemplate") })
                if (path.Length > 0)
                    text = text.Replace(path, "<model file>", StringComparison.OrdinalIgnoreCase);
        text = text.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(text, @"[A-Za-z]:\\[^\r\n""<>]*", "<local path>");
    }
    void Log(string? line)
    {
        if (line == null)
            return;
        string safe = Redact(line);
        lock (gate)
        {
            logs.Enqueue(safe);
            while (logs.Count > 500)
                logs.Dequeue();
            persistentLog?.WriteLine(safe);
            persistentLog?.Flush();
            if (firstActionableCause.Length == 0
                && Regex.IsMatch(safe, @"(?i)(\b(error|fatal|failed|assert|exception)\b|\sE\s)"))
                firstActionableCause = safe.Trim();
        }
    }
    // Reads what a file-output server (PolyStrata) wrote since the last read into the Logs view, a whole line at a time.
    void DrainOutput()
    {
        lock (outputGate)
        {
            if (outputFile.Length == 0 || outputDecoder == null)
                return;
            try
            {
                using var file = new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length <= outputRead)
                    return;
                file.Seek(outputRead, SeekOrigin.Begin);
                var bytes = new byte[Math.Min(file.Length - outputRead, 1 << 20)];
                int n = file.Read(bytes, 0, bytes.Length);
                outputRead += n;
                var text = new char[outputDecoder.GetCharCount(bytes, 0, n)];
                outputDecoder.GetChars(bytes, 0, n, text, 0);
                foreach (char c in text)
                {
                    if (c != '\n') { outputLine.Append(c); continue; }
                    Log(outputLine.ToString().TrimEnd('\r'));
                    outputLine.Clear();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    async Task TailOutput(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            DrainOutput();
            try { await Task.Delay(250, stop); }
            catch (OperationCanceledException) { }
        }
    }
    // Stops reading the output file; the server goes on writing to it.
    void StopTail()
    {
        tail?.Cancel();
        tail?.Dispose();
        tail = null;
        lock (outputGate)
        {
            outputFile = "";
            outputDecoder = null;
        }
    }
    public string LogText
    {
        get
        {
            lock (gate)
                return string.Join(Environment.NewLine, logs);
        }
    }
    // Output of launcher-driven tools (PolyStrata update/rollback) shown in the same Logs view, prefixed by source.
    public void AppendExternalLog(string source, string text)
    {
        foreach (var line in (text ?? "").Split('\n'))
            if (line.Trim().Length > 0)
                Log($"[{source}] {line.TrimEnd()}");
    }
    void ClearLive()
    {
        LiveModelId = "";
        LiveModelPath = "";
        LiveContext = null;
        LiveProfile = null;
    }
    static bool SameModelPath(string expected, string actual)
    {
        try { return Path.GetFullPath(expected).Equals(Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    async Task ReadProps(HttpClient http)
    {
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{store.Value.Port}/props");
            if (!response.IsSuccessStatusCode)
                return;
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = data.RootElement;
            if (root.TryGetProperty("model_path", out var path) && path.ValueKind == JsonValueKind.String)
                LiveModelPath = path.GetString() ?? "";
            if (root.TryGetProperty("default_generation_settings", out var settings)
                && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("n_ctx", out var context) && context.TryGetInt32(out int n) && n > 0)
                LiveContext = n;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (JsonException) { }
    }
    public Task<bool> ReconcileExisting(Profile profile) => ReconcileExisting(new[] { profile });
    public async Task<bool> ReconcileExisting(IReadOnlyList<Profile> profiles)
    {
        bool wasExisting = ExistingServer;
        ExistingServer = false;
        if (Active)
            return true;
        ClearLive();
        if (!LocalPortListening(store.Value.Port))
        {
            ClearDetached();
            if (wasExisting)
            {
                Error = "";
                SetState("Stopped");
            }
            return false;
        }
        ExistingServer = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Value.ApiKey);
            using var health = await http.GetAsync($"http://127.0.0.1:{store.Value.Port}/health");
            if (health.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Error = "A server is listening on this port, but its API key differs from the launcher key.";
                SetState("API key mismatch");
                return true;
            }
            // llama-server always serves /health (503 while loading), so a 404 means another program owns the port.
            if (health.StatusCode == HttpStatusCode.NotFound)
            {
                Error = $"Another program is using port {store.Value.Port} (it has no llama-server /health endpoint). Stop that program or choose a different port.";
                SetState("Different server on port");
                return true;
            }
            if (!health.IsSuccessStatusCode)
            {
                Error = "A server is listening on this port but is not healthy yet (HTTP " + (int)health.StatusCode + ").";
                SetState("Server not ready");
                return true;
            }
            using var response = await http.GetAsync($"http://127.0.0.1:{store.Value.Port}/v1/models");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Error = "A server is listening on this port, but its API key differs from the launcher key.";
                SetState("API key mismatch");
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                Error = "The port is occupied, but its model API returned HTTP " + (int)response.StatusCode + ".";
                SetState("Different server on port");
                return true;
            }
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // llama-server lists one ID; PolyStrata lists its own name first and then each alias under its own ID.
            var liveIds = data.RootElement.GetProperty("data").EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
            LiveModelId = liveIds.FirstOrDefault() ?? "";
            await ReadProps(http);
            LiveProfile = profiles.FirstOrDefault(p => liveIds.Contains(p.Alias)
                && (LiveModelPath.Length == 0 || SameModelPath(p.RuntimeId == Basalt.Runtime ? p.BasaltPack : p.Path, LiveModelPath)));
            if (LiveProfile != null)
            {
                LiveModelId = LiveProfile.Alias;
                if (TryAttach(LiveProfile)) return true;
                Error = $"{LiveProfile.Name} is already running on this port"
                    + (LiveContext is int ctx ? $" with {ctx:N0} context" : " (live context unavailable)")
                    + ". This launcher did not start it, so it will not start a duplicate; Stop / Unload can end it after you confirm.";
                SetState("Existing server running");
                return true;
            }
            Error = LiveModelId.Length > 0
                ? $"This port serves model ID {LiveModelId}, which does not match a registered model and path. The launcher will not start a duplicate server."
                : "This port is occupied by a server that reported no model ID.";
            SetState("Different server on port");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            Error = "The port is occupied, but the server health/model response could not be read: " + Redact(ex.Message);
            SetState("Server not responding");
        }
        return true;
    }
    public async Task Start(Profile p, ModelInfo m, string backend, EngineManager engine, Probe probe, bool safe = false)
    {
        if (Active)
            throw new InvalidOperationException("Stop the current server first.");
        ExistingServer = false;
        ClearLive();
        if (LocalPortListening(store.Value.Port))
        {
            await ReconcileExisting(store.Value.Models);
            throw new InvalidOperationException(Error.Length > 0 ? Error : "This port is already in use. The launcher will not stop or adopt another application's server.");
        }
        var launch = p;
        if (safe)
        {
            launch = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p))!;
            launch.Acceleration = "Off";
            launch.Ngram = "Off";
            launch.TurboQuantEnabled = false;
            launch.CustomArguments = "";
            launch.Options["ContextSize"] = Math.Min(8192, p.Options.I("ContextSize"));
            launch.Options["BatchSize"] = 256;
            launch.Options["UBatchSize"] = 64;
            launch.Options["CacheK"] = "Q8_0";
            launch.Options["CacheV"] = "Q8_0";
        }
        bool poly = launch.RuntimeId == PolyStrata.Runtime;
        bool basalt = launch.RuntimeId == Basalt.Runtime;
        bool external = poly || basalt;
        string polyNote = "";
        if (basalt && Basalt.Problem(store.Value.BasaltRoot, launch.BasaltPack, m) is string basaltProblem)
            throw new InvalidOperationException(basaltProblem);
        if (poly)
        {
            if (!store.Value.PolyStrataEnabled)
                throw new InvalidOperationException("PolyStrata is turned off. Enable it under Advanced, or choose another runtime for this model.");
            if (PolyStrata.InstallProblem(store.Value.PolyStrataRoot) is string problem)
                throw new InvalidOperationException(problem);
            if (!PolyStrata.Supports(m))
                throw new InvalidOperationException($"PolyStrata does not run the {m.Architecture} architecture. Use the Bundled llama.cpp runtime for this model.");
            if (!File.Exists(launch.PolyStrataConfig))
                throw new InvalidOperationException("No PolyStrata run config is selected for this model. Run PolyStrata's setup for it, then choose the runtime again.");
            PolyStrata.EnsureAlias(launch.PolyStrataConfig, p.Alias);
            polyNote = PolyStrata.ApplyLaunchSettings(launch.PolyStrataConfig, launch.Options.I("ContextSize"),
                launch.Options.S("CacheK", "Q8_0"), launch.Options.S("CacheV", "Q8_0"),
                launch.Options.I("PolyParallelSlots", PolyStrata.ReadParallel(launch.PolyStrataConfig)));
        }
        var args = poly ? PolyStrata.Arguments(store.Value, launch) : basalt ? Basalt.Arguments(store.Value, launch) : Arguments.Build(launch, store.Value, m, backend, probe.Help);
        LaunchSummary = Arguments.Sanitize(args, store.Value, p);
        string logFolder = Path.Combine(store.Root, "logs");
        Directory.CreateDirectory(logFolder);
        string logFile = Path.Combine(logFolder, $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{p.Id[..Math.Min(12, p.Id.Length)]}-{Guid.NewGuid():N}.log");
        persistentLog?.Dispose();
        persistentLog = new StreamWriter(new FileStream(logFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        firstActionableCause = "";
        Log("Starting engine. " + LaunchSummary);
        if (poly)
            Log($"PolyStrata run config: max context {launch.Options.I("ContextSize")}, KV {PolyStrata.KvFor(launch.Options.S("CacheK", "Q8_0"), launch.Options.S("CacheV", "Q8_0")).Kv}. {polyNote}");
        string executable = poly ? PolyStrata.Python(store.Value.PolyStrataRoot) : basalt ? Basalt.Python(store.Value.BasaltRoot) : engine.Executable(backend, launch);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = poly ? store.Value.PolyStrataRoot : basalt ? store.Value.BasaltRoot : Path.GetDirectoryName(executable)! };
        if (external)
        {
            start.Environment["PYTHONUNBUFFERED"] = "1";
            start.Environment["PYTHONIOENCODING"] = "utf-8"; // its output file is read back as UTF-8
        }
        if (basalt) start.Environment["PATH"] = Path.Combine(store.Value.BasaltRoot, "bin") + Path.PathSeparator + start.Environment["PATH"];
        else if (m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase) && Arguments.MtpActive(launch, m))
            start.Environment["LLAMA_ATTN_ROT_DISABLE"] = "1";
        // The experimental runtime otherwise auto-upgrades Turbo4 keys to Q8 on
        // high-GQA models. Profiles must launch the exact cache types displayed
        // by the UI, and qwen4exp's indexer cache is not upgraded in lockstep.
        if (launch.TurboQuantEnabled)
            start.Environment["TURBO_AUTO_ASYMMETRIC"] = "0";
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        EventHandler exited = (sender, _) => { if (!stopping) { DrainOutput(); Error = firstActionableCause.Length > 0 ? firstActionableCause : "Server exited with code " + ((Process)sender!).ExitCode + ". Open Logs for details."; Log(Error); SetState("Crashed"); } };
        if (!external)
        {
            process = new()
            {
                StartInfo = start,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, e) => Log(e.Data);
            process.ErrorDataReceived += (_, e) => Log(e.Data);
            process.Exited += exited;
        }
        store.Value.StartupFailed = true;
        store.Value.StartupRecovery = new()
        {
            ProfileId = p.Id,
            RuntimeId = launch.RuntimeId,
            Backend = backend,
            StartedUtc = DateTime.UtcNow.ToString("O"),
            LogFile = logFile,
            LaunchSummary = LaunchSummary
        };
        store.Save();
        stopping = false;
        Error = "";
        SetState("Loading");
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();
            var limit = new ExtendedLimit();
            limit.Basic.LimitFlags = 0x2000;
            int size = Marshal.SizeOf<ExtendedLimit>();
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limit, mem, false);
                if (!SetInformationJobObject(job, 9, mem, (uint)size))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally { Marshal.FreeHGlobal(mem); }
            if (external)
            {
                // PolyStrata keeps its own RAM budget in its engine child process. Its output goes to a file, which
                // stays writable after this launcher closes with the server running; the Logs view tails it.
                outputFile = Path.ChangeExtension(logFile, null) + "-output.log";
                outputRead = 0;
                outputDecoder = System.Text.Encoding.UTF8.GetDecoder();
                outputLine.Clear();
                process = FileOutputProcess.Start(start, outputFile, job);
                process.Exited += exited;
                process.EnableRaisingEvents = true;
                tail = new CancellationTokenSource();
                _ = TailOutput(tail.Token);
            }
            else
            {
                process!.Start();
                ApplyWorkingSetLimit(launch, m, false);
                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    process.Kill(true);
                    throw new System.ComponentModel.Win32Exception();
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Value.ApiKey);
            // PolyStrata maps a large MoE file and fills its expert cache before it answers; allow ten minutes.
            for (int i = 0, limitSeconds = external ? 600 : 300; i < limitSeconds; i++)
            {
                if (stopping)
                    return;
                if (process.HasExited)
                    throw new InvalidOperationException("Engine exited during startup (" + process.ExitCode + "). Check Logs and try safe settings.");
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{store.Value.Port}/v1/models");
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new InvalidOperationException("The server on the configured port rejected the API key (HTTP " + (int)response.StatusCode + "). Check for another server or a changed key.");
                    if (response.IsSuccessStatusCode)
                    {
                        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        var ids = data.RootElement.GetProperty("data").EnumerateArray()
                            .Select(x => x.GetProperty("id").GetString()).ToArray();
                        if (ids.Contains(p.Alias))
                        {
                            if (basalt)
                            {
                                var entry = data.RootElement.GetProperty("data").EnumerateArray()
                                    .First(x => x.GetProperty("id").GetString() == p.Alias);
                                if (!Basalt.ModelLoaded(entry))
                                { await Task.Delay(1000); continue; }
                            }
                            LiveModelId = p.Alias;
                            await ReadProps(http);
                            if (basalt && (LiveModelPath.Length == 0 || LiveContext is null))
                            { await Task.Delay(1000); continue; }
                            if (basalt && LiveModelPath.Length > 0 && !SameModelPath(p.BasaltPack, LiveModelPath))
                                throw new InvalidOperationException("Basalt reported a different model pack than the selected one.");
                            if (basalt) LiveModelPath = p.Path;
                            if (poly && LiveModelPath.Length == 0) // PolyStrata's /props has no model path
                                LiveModelPath = p.Path;
                            if (LiveModelPath.Length > 0 && !SameModelPath(p.Path, LiveModelPath))
                                throw new InvalidOperationException("The responding server reports the expected alias but a different model path. Another server may have taken the port.");
                            LiveProfile = p;
                            // The native Windows model loader may adjust its working-set
                            // quota while creating mmap-backed tensors. Reassert the hard
                            // ceiling after readiness, which is the quota that governs
                            // prompt processing and learned-PLE page faults.
                            if (!external)
                                ApplyWorkingSetLimit(launch, m, true);
                            store.Value.StartupFailed = false;
                            store.Value.StartupRecovery = null;
                            store.Save();
                            Error = LiveContext is int actual && actual != launch.Options.I("ContextSize")
                                ? $"Server is running with {actual:N0} effective context; the selected value is {launch.Options.I("ContextSize"):N0}."
                                : polyNote;
                            bool contextDiffers = LiveContext is int live && live != launch.Options.I("ContextSize");
                            SetState(contextDiffers ? "Running (context differs)" : "Running");
                            return;
                        }
                        if (ids.Length > 0)
                            throw new InvalidOperationException("The server on this port reported model ID " + ids[0] + " instead of the selected model. Check for another llama-server on the port.");
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(1000);
            }
            throw new TimeoutException(external ? "The selected engine did not become ready within ten minutes." : "Model did not become ready within five minutes.");
        }
        catch (Exception ex)
        {
            DrainOutput();
            Error = firstActionableCause.Length > 0 ? firstActionableCause : Redact(ex.Message);
            if (store.Value.StartupRecovery?.ProfileId == p.Id)
            {
                store.Value.StartupRecovery.FirstActionableCause = Error;
                store.Value.StartupRecovery.ExitCode = process?.HasExited == true ? process.ExitCode : null;
                store.Save();
            }
            await Stop();
            SetState("Crashed");
            throw new InvalidOperationException("Startup failed: " + Error, ex);
        }
    }
    public async Task Stop()
    {
        stopping = true;
        SetState("Stopping");
        if (process is { HasExited: false })
        {
            if (process.CloseMainWindow())
            {
                using var c = new CancellationTokenSource(2000);
                try
                {
                    await process.WaitForExitAsync(c.Token);
                }
                catch { }
            }
            if (!process.HasExited)
            {
                Log("Stopping launcher-owned server; this consoleless engine has no graceful shutdown endpoint.");
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
        process?.Dispose();
        process = null;
        StopTail();
        ClearDetached();
        ExistingServer = false;
        ClearLive();
        persistentLog?.Dispose();
        persistentLog = null;
        if (job != IntPtr.Zero)
        {
            CloseHandle(job);
            job = IntPtr.Zero;
        }
        SetState("Stopped");
    }
    // A server for one of this launcher's models that it did not start or cannot reconnect to: one started by a script or
    // another tool, or left by a launcher that lost its record. Only the model's own engine process is ever stopped.
    public bool CanStopExisting => !Active && ExistingServer && LiveProfile != null && State == "Existing server running";
    public async Task StopExisting()
    {
        if (!CanStopExisting || PortOwner(store.Value.Port) is not int owner || owner <= 0)
            throw new InvalidOperationException("No server for a registered model is running on port " + store.Value.Port + ".");
        string name = LiveProfile!.Name;
        using var server = Process.GetProcessById(owner);
        string executable;
        try { executable = Path.GetFileNameWithoutExtension(server.MainModule?.FileName ?? ""); }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException($"The server on port {store.Value.Port} runs with higher privileges than the launcher. Stop it from an administrator window."); }
        // llama-server listens itself; PolyStrata's port belongs to its Python interpreter, whose tree holds strata.exe.
        if (!new[] { "llama-server", "python", "pythonw" }.Contains(executable, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Port {store.Value.Port} belongs to {executable}.exe, not a llama.cpp or PolyStrata server; the launcher will not stop it.");
        SetState("Stopping");
        Log($"Stopping the {name} server that this launcher did not start (process {owner}, {executable}.exe).");
        server.Kill(true);
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await server.WaitForExitAsync(wait.Token);
        // A PolyStrata venv stub exits once its interpreter has; the port is free only after that.
        for (int i = 0; i < 30 && LocalPortListening(store.Value.Port); i++)
            await Task.Delay(1000);
        ClearDetached();
        ExistingServer = false;
        ClearLive();
        Error = "";
        SetState("Stopped");
    }
    public void Detach()
    {
        DrainOutput();
        if (Active)
        {
            if (LiveProfile == null || LiveModelPath.Length == 0)
                throw new InvalidOperationException("The running model identity could not be saved for reconnection.");
            int? owner = PortOwner(store.Value.Port);
            int serverId = ServerProcessId(process!.Id, owner, owner is int o && o != process.Id ? ParentOf(o) : null);
            using var child = serverId != process.Id ? Process.GetProcessById(serverId) : null;
            Remember(child ?? process, LiveProfile);
        }
        if (Active && job != IntPtr.Zero)
        {
            // A normal UI close leaves the healthy server available for the next
            // launcher instance. An abrupt launcher exit still closes the job
            // with KILL_ON_JOB_CLOSE, as covered by the ownership test.
            var limit = new ExtendedLimit();
            int size = Marshal.SizeOf<ExtendedLimit>();
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limit, mem, false);
                if (!SetInformationJobObject(job, 9, mem, (uint)size))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not leave the server running. The launcher remains open so you can stop it explicitly.");
            }
            finally { Marshal.FreeHGlobal(mem); }
            Log("Launcher is closing; server continues on port " + store.Value.Port + ".");
        }
        stopping = true;
        StopTail();
        persistentLog?.Dispose();
        persistentLog = null;
        if (job != IntPtr.Zero)
        {
            CloseHandle(job);
            job = IntPtr.Zero;
        }
        process?.Dispose();
        process = null;
        ClearLive();
    }
    public string Report(Hardware h, ModelInfo? m, Profile? p, string backend, string version)
    {
        // Runtime output is deliberately excluded: engines may print prompt fragments or vocabulary.
        var report = new
        {
            Application = "llama.cpp Launcher",
            Version = "1.2.1",
            Engine = version,
            Windows = Environment.OSVersion.ToString(),
            Hardware = h,
            Backend = backend,
            Model = m == null ? null : new
            {
                m.Architecture,
                m.Quantization,
                m.Bytes,
                m.Parameters,
                m.NativeContext,
                m.Mtp,
                m.HasTemplate,
                m.Shards
            },
            Configuration = p == null ? null : new
            {
                Context = p.Options.I("ContextSize"),
                K = p.Options.S("CacheK"),
                V = p.Options.S("CacheV"),
                Layers = p.Options.S("GpuLayers"),
                p.MemorySafety,
                p.Acceleration,
                p.Ngram
            },
            State,
            StartupError = Error,
            LaunchConfiguration = LaunchSummary,
            RecentLauncherEvents = EventSnapshot(),
            Privacy = "Runtime output, model names, paths, API keys, prompts and conversations are excluded."
        };
        return Redact(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    string[] EventSnapshot()
    {
        lock (gate)
            return events.ToArray();
    }
    public void Dispose()
    {
        stopping = true;
        StopTail();
        if (job != IntPtr.Zero)
        {
            CloseHandle(job);
            job = IntPtr.Zero;
        }
        process?.Dispose();
        persistentLog?.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr Minimum, Maximum; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessWorkingSetSizeEx(IntPtr process, UIntPtr minimum, UIntPtr maximum, uint flags);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
