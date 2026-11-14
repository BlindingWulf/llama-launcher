using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Headers;

namespace LlamaLauncher;
public static class SelfTests
{
    public static int Dropdowns(string output)
    {
        Directory.CreateDirectory(output);
        var store = new ConfigStore(Path.Combine(output, "data"), false);
        var p = new Profile { Name = "Dropdown fixture" };
        store.Value.Models.Add(p);
        store.Value.ActiveId = p.Id;
        var engine = new EngineManager(AppContext.BaseDirectory, store.Root);
        var results = new List<object>();
        try
        {
            foreach (int dpi in new[] { 96, 120, 144, 168, 192 })
            {
                using var form = new MainForm(store, engine, true);
                form.DropdownsOnlyForTest(Path.Combine(output, "dpi-" + dpi), dpi);
                results.Add(new { dpi, dropdowns = 16, themes = 3, passed = true });
            }
            File.WriteAllText(Path.Combine(output, "dropdown-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            results.Add(new { error = ex.ToString() });
            File.WriteAllText(Path.Combine(output, "dropdown-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
    // --perf-test <output> [model.gguf]: page switching and resize timings in an isolated settings folder.
    public static int Performance(string output, string? model)
    {
        Directory.CreateDirectory(output);
        var store = new ConfigStore(Path.Combine(output, "data-" + Guid.NewGuid().ToString("N")), false);
        if (model != null && File.Exists(model))
        {
            var p = new Profile { Name = Path.GetFileNameWithoutExtension(model), Path = model };
            store.Value.Models.Add(p);
            store.Value.ActiveId = p.Id;
        }
        var engine = new EngineManager(AppContext.BaseDirectory, store.Root);
        using var form = new MainForm(store, engine, true);
        string report = form.PerformanceForTest();
        File.WriteAllText(Path.Combine(output, "performance.txt"), report);
        return 0;
    }
    public static async Task<int> RepairFlashNext(string modelPath, string mtpPath)
    {
        var model = GgufInspector.Inspect(modelPath);
        var mtp = GgufInspector.Inspect(mtpPath);
        if (!model.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected base model is not qwen4exp Flash-Next.");
        if (mtp.Mtp != "Supported" || new FileInfo(mtpPath).Length != 2_622_313_344L)
            throw new InvalidDataException("The selected file is not the verified Flash-Next MTP head.");
        var store = new ConfigStore();
        var profile = store.Value.Models.FirstOrDefault(p => p.Path.Equals(modelPath, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            profile = new Profile { Name = "Qwen3.8 Flash Next", Path = modelPath, Alias = "qwen38-flash-next", MemorySafety = "Balanced" };
            store.Value.Models.Add(profile);
        }
        profile.Name = "Qwen3.8 Flash Next";
        profile.Alias = "qwen38-flash-next";
        profile.RuntimeId = EngineManager.FlashNextRuntime;
        profile.DraftModel = mtpPath;
        profile.Acceleration = "On";
        profile.Ngram = "ngram-mod";
        profile.TurboQuantEnabled = true;
        profile.TurboQuantPreset = "Quality-first";
        profile.TurboQuantK = "Q8_0";
        profile.TurboQuantV = "Turbo4";
        profile.Options["ContextSize"] = 65536;
        profile.Options["NativeContextLimit"] = model.NativeContext;
        profile.Options["CacheK"] = "Q8_0";
        profile.Options["CacheV"] = "Q8_0";
        profile.Options["GpuLayers"] = "ALL";
        profile.Options["CpuMoe"] = true;
        profile.Options["Threads"] = 8;
        profile.Options["ThreadsBatch"] = 8;
        profile.Options["BatchSize"] = 4096;
        profile.Options["UBatchSize"] = 2048;
        profile.Options["FlashAttention"] = true;
        profile.Options["MtpDraftMax"] = 2;
        profile.Options["MtpDraftPMin"] = 0.75;
        profile.Options["PromptCacheMiB"] = 1024;
        profile.Options["MaxOutputTokens"] = 8192;
        store.Value.ActiveId = profile.Id;
        store.Value.ShowTurboQuantControls = true;
        var engine = new EngineManager(AppContext.BaseDirectory, store.Root);
        engine.EnsureCompatibleProfile(profile, model);
        store.Save();
        if (File.Exists(store.Value.LegacyCommon) && File.Exists(store.Value.LegacySettings))
        {
            var hardware = Hardware.Detect();
            string backend = await engine.Resolve("CUDA", hardware, profile);
            await Integration.SyncQwen(store, profile, engine.Executable(backend, profile));
        }
        return 0;
    }

    public static async Task<int> ValidateProfile(string root, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var store = new ConfigStore(root, false);
        var profile = store.Value.Models.FirstOrDefault(p => p.Id == store.Value.ActiveId)
            ?? store.Value.Models.FirstOrDefault()
            ?? throw new InvalidOperationException("Validation settings contain no model profile.");
        var model = GgufInspector.Inspect(profile.Path);
        var hardwareBefore = Hardware.Detect();
        var engine = new EngineManager(AppContext.BaseDirectory, store.Root);
        bool routed = engine.EnsureCompatibleProfile(profile, model);
        string backend = await engine.Resolve(store.Value.Backend, hardwareBefore, profile);
        if (backend != "CUDA")
            throw new InvalidOperationException("Real-model validation requires CUDA; resolved backend was " + backend + ".");
        var probe = await engine.Inspect(backend, profile);
        store.Save();
        using var runner = new RuntimeManager(store);
        var samples = new List<object>();
        bool reopenedLauncherDetected = false;
        Exception? failure = null;
        try
        {
            await runner.Start(profile, model, backend, engine, probe);
            using (var reopened = new RuntimeManager(new ConfigStore(root, false)))
                reopenedLauncherDetected = await reopened.ReconcileExisting(profile) && reopened.ExistingServer;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Value.ApiKey);
            var cases = new[]
            {
                ("sequence", "Return the integers 1 through 40, separated only by single commas. Do not add spaces or commentary.", string.Join(',', Enumerable.Range(1, 40))),
                ("strict-json", "Return only this exact minified JSON and nothing else: {\"status\":\"ok\",\"count\":3,\"items\":[\"alpha\",\"beta\",\"gamma\"]}", "{\"status\":\"ok\",\"count\":3,\"items\":[\"alpha\",\"beta\",\"gamma\"]}"),
                ("repeat", "Repeat exactly, with no commentary: Pack my box with five dozen liquor jugs. Pack my box with five dozen liquor jugs. Pack my box with five dozen liquor jugs.", "Pack my box with five dozen liquor jugs. Pack my box with five dozen liquor jugs. Pack my box with five dozen liquor jugs.")
            };
            foreach (var sample in cases.Concat(cases.TakeLast(1)))
            {
                var body = JsonSerializer.Serialize(new
                {
                    model = profile.Alias,
                    temperature = 0,
                    max_tokens = 256,
                    messages = new[] { new { role = "user", content = sample.Item2 } }
                });
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var response = await http.PostAsync($"http://127.0.0.1:{store.Value.Port}/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
                string raw = await response.Content.ReadAsStringAsync();
                clock.Stop();
                response.EnsureSuccessStatusCode();
                var json = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidDataException("Empty inference response.");
                string content = json["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                samples.Add(new
                {
                    id = sample.Item1,
                    passed = content.Trim() == sample.Item3,
                    elapsed_ms = clock.ElapsedMilliseconds,
                    prompt_tokens = json["usage"]?["prompt_tokens"]?.GetValue<int>(),
                    completion_tokens = json["usage"]?["completion_tokens"]?.GetValue<int>(),
                    timings = json["timings"]?.DeepClone(),
                    content
                });
            }
        }
        catch (Exception ex) { failure = ex; }
        var hardwareDuring = Hardware.Detect();
        var result = new
        {
            captured_at = DateTimeOffset.Now,
            backend,
            routed_from_incompatible_runtime = routed,
            runtime = profile.RuntimeId,
            context = profile.Options.I("ContextSize"),
            cache = Arguments.EffectiveCache(profile, model),
            profile.Acceleration,
            profile.Ngram,
            process_id = runner.ProcessId,
            state = runner.State,
            reopened_launcher_detected = reopenedLauncherDetected,
            model = new { model.Architecture, model.Quantization, model.NativeContext, model.Shards },
            hardware_before = hardwareBefore,
            hardware_during = hardwareDuring,
            launch = runner.LaunchSummary,
            samples,
            log = runner.LogText,
            error = failure?.ToString()
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        await runner.Stop();
        return failure == null && reopenedLauncherDetected && samples.Count == 4 && samples.All(x => (bool)(x.GetType().GetProperty("passed")?.GetValue(x) ?? false)) ? 0 : 1;
    }

    // Real-model check, run on the GPU, of every way the launcher ends a server it started in an earlier session.
    // Pass 1: start, close the window (Detach), reconnect from a fresh launcher, answer one request, end that launcher
    // abruptly (a crash or Task Manager), reconnect again and Stop. Pass 2: start, close, lose the reconnect record (a
    // server the launcher can no longer claim, as when a script started it), then the confirmed Stop for such servers.
    // Each pass must free the port and leave no process of the server's tree (for PolyStrata: the venv stub, the
    // interpreter, strata.exe and its vision helper).
    public static async Task<int> ValidateDetach(string root, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var store = new ConfigStore(root, false);
        var profile = store.Value.Models.FirstOrDefault(p => p.Id == store.Value.ActiveId)
            ?? throw new InvalidOperationException("Validation settings have no active model profile.");
        var model = GgufInspector.Inspect(profile.Path);
        var engine = new EngineManager(AppContext.BaseDirectory, store.Root);
        engine.EnsureCompatibleProfile(profile, model);
        string backend = await engine.Resolve(store.Value.Backend, Hardware.Detect(), profile);
        var probe = await engine.Inspect(backend, profile);
        store.Save();
        int port = store.Value.Port;
        var trees = new List<List<(int Id, string Name)>>();
        bool reattached = false, answered = false, reattachedAfterCrash = false, stopOffered = false;
        bool portFreeAfterStop = false, portFreeAfterConfirmedStop = false;
        int? firstId = null;
        string reply = "", firstLog = "", launch = "";
        Exception? failure = null;
        async Task<bool> PortFreed()
        {
            for (int i = 0; i < 30; i++)
            {
                if (!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port))
                    return true;
                await Task.Delay(1000);
            }
            return false;
        }
        // Starts the profile in one launcher session and closes it with the server running, recording the server's tree.
        async Task StartAndClose()
        {
            using var first = new RuntimeManager(new ConfigStore(root, false));
            await first.Start(profile, model, backend, engine, probe);
            int launched = first.ProcessId!.Value;
            firstId ??= launched;
            launch = first.LaunchSummary;
            var tree = new List<(int Id, string Name)> { (launched, "launched") };
            var pending = new Queue<int>(new[] { launched });
            while (pending.Count > 0)
            {
                int id = pending.Dequeue();
                using var query = new System.Management.ManagementObjectSearcher($"SELECT ProcessId,Name FROM Win32_Process WHERE ParentProcessId={id}");
                foreach (System.Management.ManagementObject item in query.Get())
                {
                    int child = Convert.ToInt32(item["ProcessId"]);
                    tree.Add((child, item["Name"]?.ToString() ?? ""));
                    pending.Enqueue(child);
                }
            }
            trees.Add(tree);
            first.Detach();
            firstLog = first.LogText;
        }
        try
        {
            await StartAndClose();
            var reopenedStore = new ConfigStore(root, false);
            var reopened = new RuntimeManager(reopenedStore, engine);
            await reopened.ReconcileExisting(reopenedStore.Value.Models);
            reattached = reopened.Active && reopened.LiveProfile?.Id == profile.Id;
            int? serverId = reopened.ProcessId;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Value.ApiKey);
                var body = JsonSerializer.Serialize(new
                {
                    model = profile.Alias, temperature = 0, max_tokens = 64,
                    chat_template_kwargs = new { enable_thinking = false },
                    messages = new[] { new { role = "user", content = "Reply with exactly the word: ready" } }
                });
                using var response = await http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
                string raw = await response.Content.ReadAsStringAsync();
                reply = JsonNode.Parse(raw)?["choices"]?[0]?["message"]?["content"]?.ToString() ?? raw[..Math.Min(raw.Length, 300)];
                answered = response.IsSuccessStatusCode && reply.Contains("ready", StringComparison.OrdinalIgnoreCase);
            }
            reopened.Dispose(); // The reopened launcher ends abruptly: no Detach runs.
            var afterCrashStore = new ConfigStore(root, false);
            using (var afterCrash = new RuntimeManager(afterCrashStore, engine))
            {
                await afterCrash.ReconcileExisting(afterCrashStore.Value.Models);
                reattachedAfterCrash = afterCrash.Active && serverId != null && afterCrash.ProcessId == serverId;
                if (reattachedAfterCrash)
                    await afterCrash.Stop();
            }
            portFreeAfterStop = await PortFreed();
            if (portFreeAfterStop)
            {
                await Task.Delay(3000);
                await StartAndClose();
                var strangerStore = new ConfigStore(root, false);
                strangerStore.Value.DetachedServer = null; // Nothing to reconnect by: a script started it, or settings were replaced.
                strangerStore.Save();
                using var stranger = new RuntimeManager(strangerStore); // and no engine, so no legacy process match either
                await stranger.ReconcileExisting(strangerStore.Value.Models);
                stopOffered = !stranger.Active && stranger.CanStopExisting;
                if (stopOffered)
                    await stranger.StopExisting();
                portFreeAfterConfirmedStop = await PortFreed();
            }
        }
        catch (Exception ex) { failure = ex; }
        await Task.Delay(2000);
        static bool Alive(int id)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(id); return !p.HasExited; }
            catch (ArgumentException) { return false; }
        }
        var survivors = trees.SelectMany(t => t).Where(t => Alive(t.Id)).ToList();
        foreach (var s in survivors) // A failed check must not leave a model loaded for the next case.
            try { using var p = System.Diagnostics.Process.GetProcessById(s.Id); p.Kill(true); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            captured_at = DateTimeOffset.Now,
            runtime = profile.RuntimeId,
            context = profile.Options.I("ContextSize"),
            launch,
            launched_process = firstId,
            process_tree = trees.FirstOrDefault()?.Select(t => new { t.Id, t.Name }),
            reattached,
            answered,
            reply,
            reattached_after_crash = reattachedAfterCrash,
            port_free_after_stop = portFreeAfterStop,
            stop_offered_without_record = stopOffered,
            port_free_after_confirmed_stop = portFreeAfterConfirmedStop,
            survivors = survivors.Select(t => new { t.Id, t.Name }),
            first_log = firstLog,
            error = failure?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure == null && reattached && answered && reattachedAfterCrash && portFreeAfterStop
            && stopOffered && portFreeAfterConfirmedStop && survivors.Count == 0 ? 0 : 1;
    }

    public static async Task<int> OwnershipHost(string path, string root)
    {
        var s = new ConfigStore(root, false);
        s.Value.Port = 18089;
        var e = new EngineManager(AppContext.BaseDirectory, root);
        using var runner = new RuntimeManager(s);
        var p = new Profile { Path = path, Alias = "ownership-test", Acceleration = "Off" };
        p.Options["ContextSize"] = 256;
        p.Options["GpuLayers"] = "0";
        await runner.Start(p, GgufInspector.Inspect(path), "CPU", e, await e.Inspect("CPU"));
        File.WriteAllText(Path.Combine(root, "server.pid"), runner.ProcessId.ToString());
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
    public static async Task<int> DetachHost(string path, string root)
    {
        var store = new ConfigStore(root, false);
        store.Value.Port = 18090;
        var profile = new Profile { Path = path, Alias = "detached-test", Name = "Detached acceptance model", Acceleration = "Off" };
        profile.Options["ContextSize"] = 2048;
        profile.Options["GpuLayers"] = "0";
        store.Value.Models.Add(profile);
        store.Value.ActiveId = profile.Id;
        store.Save();
        var engine = new EngineManager(AppContext.BaseDirectory, root);
        using var runner = new RuntimeManager(store);
        await runner.Start(profile, GgufInspector.Inspect(path), "CPU", engine, await engine.Inspect("CPU"));
        int serverPid = runner.ProcessId ?? throw new InvalidOperationException("Detached server has no process ID.");
        runner.Detach();
        File.WriteAllText(Path.Combine(root, "detached-server.pid"), serverPid.ToString());
        return 0;
    }
    static readonly List<object> results = new();
    static void Check(string name, bool passed)
    {
        results.Add(new
        {
            name,
            passed
        });
        if (!passed)
            throw new Exception("FAILED: " + name);
    }
    public static async Task<int> Run(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "llama-launcher-tests"));
        Directory.CreateDirectory(output);
        string root = Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ConfigStore(root, false);
            Check("Fresh LAN disabled", !store.Value.Lan);
            Check("Fresh model Tailscale sharing disabled", !store.Value.ModelTailscale);
            using (var routes = JsonDocument.Parse("""{"TCP":{"8080":{"TCPForward":"127.0.0.1:8080"},"443":{"HTTPS":true},"9000":{"TCPForward":"127.0.0.1:3000"}}}"""))
            {
                Check("Model sharing recognizes its loopback target", ModelTailnet.OwnRoute(routes.RootElement, 8080));
                Check("Model sharing preserves unrelated routes", !ModelTailnet.OwnRoute(routes.RootElement, 443) && !ModelTailnet.OwnRoute(routes.RootElement, 9000) && !ModelTailnet.OwnRoute(routes.RootElement, 8081));
            }

            Check("Dark theme by default", store.Value.Theme == "Dark");
            Check("Balanced is the new-profile memory default", store.Value.DefaultMemorySafety == "Balanced");
            var independent = new ConfigStore(Path.Combine(root, "independent-device"));
            Check("Fresh devices have distinct API keys and no network/model state", independent.Value.ApiKey != store.Value.ApiKey && independent.Value.ApiKey.Length == 64 && !independent.Value.Lan && !independent.Value.CheckUpdatesOnOpen && independent.Value.Models.Count == 0 && independent.Value.DetachedServer == null && independent.Value.LegacyCommon == "" && independent.Value.LegacySettings == "");
            string brokenRoot = Path.Combine(root, "both-invalid");
            Directory.CreateDirectory(brokenRoot);
            File.WriteAllText(Path.Combine(brokenRoot, "settings.json"), "broken-primary");
            File.WriteAllText(Path.Combine(brokenRoot, "settings.json.bak"), "broken-backup");
            var bothInvalid = new ConfigStore(brokenRoot);
            Check("Corrupt settings and backup do not block startup or discard originals", bothInvalid.Value.Models.Count == 0 && !bothInvalid.Value.Lan && Directory.GetFiles(brokenRoot, "*.invalid-*").Length == 2);
            string foreignLegacy = Path.Combine(root, "foreign-qwen-settings.json");
            const string foreignContents = "{\"SchemaVersion\":2,\"Models\":[]}";
            File.WriteAllText(foreignLegacy, foreignContents);
            var foreignStore = new ConfigStore(Path.Combine(root, "foreign-import"), true, foreignLegacy);
            Check("Unsupported optional Qwen import does not block a fresh install", foreignStore.Value.Models.Count == 0 && foreignStore.Notice.Contains("left unchanged", StringComparison.Ordinal) && File.ReadAllText(foreignLegacy) == foreignContents);
            store.Save();
            Check("Settings persist", new ConfigStore(root, false).Value.ApiKey == store.Value.ApiKey);
            var pruneStore = new ConfigStore(Path.Combine(root, "prune"), false);
            string presentGguf = Path.Combine(root, "present.gguf");
            File.WriteAllText(presentGguf, "x");
            char? absentDrive = "QRSTUVWXYZ".Select(c => (char?)c).FirstOrDefault(c => !Directory.Exists($"{c}:\\"));
            var kept = new Profile { Name = "Present", Path = presentGguf };
            var deleted = new Profile { Name = "Deleted", Path = Path.Combine(root, "deleted.gguf") };
            var unplugged = new Profile { Name = "Unplugged", Path = absentDrive is char d ? $"{d}:\\models\\x.gguf" : presentGguf };
            var live = new Profile { Name = "Live", Path = Path.Combine(root, "live-deleted.gguf") };
            pruneStore.Value.Models.AddRange(new[] { kept, deleted, unplugged, live });
            pruneStore.Value.ActiveId = deleted.Id;
            var pruned = pruneStore.PruneMissingModels(live.Id);
            var reloaded = new ConfigStore(pruneStore.Root, false).Value;
            string polyRoot = Path.Combine(root, "polystrata");
            Directory.CreateDirectory(polyRoot);
            string shard = @"C:\AI\Models\Qwen 3.8 Flash\Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf";
            string polyConfig = Path.Combine(polyRoot, "strata-unsloth-iq4xs.json");
            File.WriteAllText(polyConfig, new JsonObject { ["exe"] = "engine/strata.exe", ["args"] = new JsonArray("--gguf", shard.Replace("00001-of", "00002-of")), ["aliases"] = new JsonArray("flash") }.ToJsonString());
            File.WriteAllText(Path.Combine(polyRoot, "strata-unsloth-iq4xs.shared-settings.json"), "{\"temperature\":1}");
            File.WriteAllText(Path.Combine(polyRoot, "strata-other.json"), new JsonObject { ["exe"] = "engine/strata.exe", ["args"] = new JsonArray("--gguf", @"D:\elsewhere\gemma.gguf") }.ToJsonString());
            Check("PolyStrata offers only the MoE architectures it implements",
                PolyStrata.Supports(new ModelInfo { Architecture = "qwen4exp" }) && PolyStrata.Supports(new ModelInfo { Architecture = "QWEN35MOE" })
                && !PolyStrata.Supports(new ModelInfo { Architecture = "qwen35" }) && !PolyStrata.Supports(new ModelInfo { Architecture = "llama" }));
            Check("PolyStrata run configs are found and matched to any shard of the GGUF; settings files are not configs",
                PolyStrata.Configs(polyRoot).Count() == 2 && PolyStrata.MatchConfig(polyRoot, shard) == polyConfig
                && PolyStrata.MatchConfig(polyRoot, @"E:\x\unrelated.gguf") == null);
            PolyStrata.EnsureAlias(polyConfig, "local-abc");
            PolyStrata.EnsureAlias(polyConfig, "local-abc");
            var aliases = JsonNode.Parse(File.ReadAllText(polyConfig))!["aliases"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            Check("The launcher alias is added to the run config once, keeping existing aliases and a backup",
                aliases.SequenceEqual(new[] { "flash", "local-abc" }) && File.Exists(polyConfig + ".bak"));
            Check("Launcher KV cache choices map to PolyStrata's nearest type at or above that precision",
                PolyStrata.KvFor("Q8_0", "Q8_0") == ("int8", "") && PolyStrata.KvFor("Q8_0", "Q4_0") == ("k8v4", "")
                && PolyStrata.KvFor("Q4_0", "Q4_0") == ("q4_0", "") && PolyStrata.KvFor("F16", "F16").Kv == "int8"
                && PolyStrata.KvFor("F16", "F16").Note.Length > 0 && PolyStrata.KvFor("Q4_0", "Q8_0").Kv == "int8"
                && PolyStrata.KvFor("Q8_0", "IQ4_NL").Kv == "k8v4");
            string ctxConfig = Path.Combine(polyRoot, "strata-ctx.json");
            File.WriteAllText(ctxConfig, new JsonObject { ["exe"] = "e", ["args"] = new JsonArray("--pack", "p", "--max-context", "131072", "--kv", "int8", "--spec", "4") }.ToJsonString());
            PolyStrata.ApplyLaunchSettings(ctxConfig, 65536, "Q8_0", "Q4_0");
            var ctxArgs = JsonNode.Parse(File.ReadAllText(ctxConfig))!["args"]!.AsArray().Select(x => x!.ToString()).ToList();
            PolyStrata.ApplyLaunchSettings(ctxConfig, 8192, "Q8_0", "Q8_0");
            var smallArgs = JsonNode.Parse(File.ReadAllText(ctxConfig))!["args"]!.AsArray().Select(x => x!.ToString()).ToList();
            Check("The launcher's context and KV cache controls are written into the PolyStrata run config",
                ctxArgs[ctxArgs.IndexOf("--max-context") + 1] == "65536" && ctxArgs[ctxArgs.IndexOf("--kv") + 1] == "k8v4"
                && ctxArgs.Contains("--spec") && smallArgs[smallArgs.IndexOf("--max-context") + 1] == "8192" && !smallArgs.Contains("--kv"));
            PolyStrata.ApplyLaunchSettings(ctxConfig, 8192, "Q8_0", "Q8_0", 8);
            Check("PolyStrata parallel slots are persisted without changing the engine arguments", PolyStrata.ReadParallel(ctxConfig) == 8 && JsonNode.Parse(File.ReadAllText(ctxConfig))!["args"]!.AsArray().Select(x => x!.ToString()).SequenceEqual(smallArgs));
            PolyStrata.ApplyLaunchSettings(ctxConfig, 8192, "Q8_0", "Q8_0", 4);
            Check("PolyStrata parallel slots can be reduced", PolyStrata.ReadParallel(ctxConfig) == 4);
            Check("Loopback and wildcard listeners identify the local model server, but tailnet listeners do not",
                RuntimeManager.LocalServerAddress(System.Net.IPAddress.Loopback) && RuntimeManager.LocalServerAddress(System.Net.IPAddress.Any)
                && RuntimeManager.LocalServerAddress(System.Net.IPAddress.IPv6Any) && !RuntimeManager.LocalServerAddress(System.Net.IPAddress.Parse("192.0.2.1")));
            var polySettings = new Settings { PolyStrataRoot = polyRoot, Port = 8123, ApiKey = "k" };
            var polyArgs = PolyStrata.Arguments(polySettings, new Profile { PolyStrataConfig = polyConfig });
            Check("PolyStrata starts its server on the launcher port, bound to this PC, with the launcher key",
                polyArgs[0].EndsWith(Path.Combine("serve", "server.py")) && polyArgs.Contains("--config") && polyArgs[polyArgs.IndexOf("--port") + 1] == "8123"
                && polyArgs[polyArgs.IndexOf("--host") + 1] == "127.0.0.1" && polyArgs[polyArgs.IndexOf("--api-key") + 1] == "k");
            var polyStore = new ConfigStore(Path.Combine(root, "poly-settings"), false);
            polyStore.Value.PolyStrataEnabled = true;
            polyStore.Value.Models.Add(new Profile { Name = "Flash", RuntimeId = PolyStrata.Runtime, PolyStrataConfig = polyConfig });
            polyStore.Save();
            var polyReloaded = new ConfigStore(polyStore.Root, false).Value;
            Check("The PolyStrata runtime and switch survive a settings reload",
                polyReloaded.PolyStrataEnabled && polyReloaded.Models[0].RuntimeId == PolyStrata.Runtime && polyReloaded.Models[0].PolyStrataConfig == polyConfig);
            polyStore.Value.Models[0].RuntimeId = PolyStrata.LegacyRuntime;
            polyStore.Save();
            var polyMigrated = new ConfigStore(polyStore.Root, false).Value;
            Check("Existing experimental PolyStrata profiles migrate without losing their config",
                polyMigrated.PolyStrataEnabled && polyMigrated.Models[0].RuntimeId == PolyStrata.Runtime && polyMigrated.Models[0].PolyStrataConfig == polyConfig);
            string basaltRoot = Path.Combine(root, "basalt");
            Directory.CreateDirectory(Path.Combine(basaltRoot, ".venv", "Scripts"));
            Directory.CreateDirectory(Path.Combine(basaltRoot, "bin"));
            Directory.CreateDirectory(Path.Combine(basaltRoot, "serve"));
            File.WriteAllText(Basalt.Python(basaltRoot), "fixture");
            File.WriteAllText(Basalt.Engine(basaltRoot), "fixture");
            File.WriteAllText(Path.Combine(basaltRoot, "serve", "server.py"), "fixture");
            string basaltPack = Path.Combine(root, "model.basalt");
            using (var fs = File.Create(basaltPack))
            using (var writer = new BinaryWriter(fs))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("BASALTMF")); writer.Write((uint)2); writer.Write((uint)5); writer.Write((ulong)(64 + 5 * 128));
                writer.Write(new byte[40]);
                foreach (string section in new[] { "vision/mmproj", "mtp/dense", "mtp/dense.index", "mtp/experts", "mtp/draft_vocab" })
                {
                    byte[] name = new byte[64]; System.Text.Encoding.ASCII.GetBytes(section).CopyTo(name, 0);
                    writer.Write(name); writer.Write((ulong)64); writer.Write((ulong)1); writer.Write(new byte[48]);
                }
            }
            var basaltProfile = new Profile { RuntimeId = Basalt.Runtime, BasaltPack = basaltPack, Alias = "flash" };
            basaltProfile.Options["ContextSize"] = 65536;
            var basaltSettings = new Settings { BasaltRoot = basaltRoot, Port = 8123, ApiKey = "fixture-key" };
            var basaltArgs = Basalt.Arguments(basaltSettings, basaltProfile);
            Check("Basalt validates model architecture and pack and launches vision with the saved API and context",
                Basalt.Problem(basaltRoot, basaltPack, new ModelInfo { Architecture = "qwen4exp" }) == null
                && Basalt.Problem(basaltRoot, basaltPack, new ModelInfo { Architecture = "llama" }) != null
                && basaltArgs[basaltArgs.IndexOf("--vision") + 1] == "dynamic"
                && basaltArgs[basaltArgs.IndexOf("--max-context") + 1] == "65536"
                && basaltArgs[basaltArgs.IndexOf("--spec") + 1] == "8"
                && basaltArgs[basaltArgs.IndexOf("--port") + 1] == "8123"
                && basaltArgs[basaltArgs.IndexOf("--api-key") + 1] == "fixture-key"
                && basaltArgs[basaltArgs.IndexOf("--conversation-cache-disk-mib") + 1] == Basalt.ConversationCacheDiskMib.ToString());
            string badBasaltPack = Path.Combine(root, "incomplete.basalt");
            File.Copy(basaltPack, badBasaltPack);
            using (var fs = new FileStream(badBasaltPack, FileMode.Open, FileAccess.Write))
            { fs.Position = 64; fs.Write(System.Text.Encoding.ASCII.GetBytes("missing")); }
            Check("Basalt rejects a pack missing its vision projector", Basalt.Problem(basaltRoot, badBasaltPack, new ModelInfo { Architecture = "qwen4exp" })?.Contains("projector") == true);
            polyStore.Value.Models.Add(basaltProfile);
            polyStore.Value.BasaltRoot = basaltRoot;
            polyStore.Save();
            var basaltReloaded = new ConfigStore(polyStore.Root, false).Value;
            Check("Basalt selection, folder and pack survive settings reload", basaltReloaded.BasaltRoot == basaltRoot
                && basaltReloaded.Models[1].RuntimeId == Basalt.Runtime && basaltReloaded.Models[1].BasaltPack == basaltPack);
            using var loadingBasalt = JsonDocument.Parse("{\"status\":{\"value\":\"loading\"}}");
            using var loadedBasalt = JsonDocument.Parse("{\"status\":{\"value\":\"loaded\"}}");
            using var missingBasaltStatus = JsonDocument.Parse("{}");
            Check("Basalt is ready only after its model reports loaded", !Basalt.ModelLoaded(loadingBasalt.RootElement)
                && Basalt.ModelLoaded(loadedBasalt.RootElement) && !Basalt.ModelLoaded(missingBasaltStatus.RootElement));
            Check("A detached server is recorded as the process that owns the port: a venv stub's direct child, never a stranger",
                RuntimeManager.ServerProcessId(100, 100, 1) == 100 && RuntimeManager.ServerProcessId(100, 200, 100) == 200
                && RuntimeManager.ServerProcessId(100, 200, 999) == 100 && RuntimeManager.ServerProcessId(100, null, null) == 100);
            Check("Deleted model files are pruned and archived; unplugged drives and the live model are kept",
                pruned.Count == 1 && pruned[0].Id == deleted.Id && reloaded.Models.Count == 3
                && reloaded.Models.All(m => m.Id != deleted.Id) && reloaded.ActiveId == kept.Id
                && File.ReadAllText(pruneStore.RemovedModelsPath).Contains(deleted.Id));
            var foreignListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            foreignListener.Start();
            var foreignStore404 = new ConfigStore(Path.Combine(root, "foreign-port"), false);
            foreignStore404.Value.Port = ((System.Net.IPEndPoint)foreignListener.LocalEndpoint).Port;
            var foreignServer = Task.Run(async () =>
            {
                // A static file server answers every unknown path, including /health, with 404.
                while (true)
                {
                    using var client = await foreignListener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    await stream.ReadAsync(new byte[4096]);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
            });
            using (var foreignObserver = new RuntimeManager(foreignStore404))
                Check("A non-llama server answering 404 on the port is reported as another program", await foreignObserver.ReconcileExisting(new Profile { Alias = "any", Path = "any.gguf" }) && foreignObserver.ExistingServer && foreignObserver.State == "Different server on port" && foreignObserver.Error.Contains("Another program", StringComparison.Ordinal) && !foreignObserver.CanStopExisting);
            foreignListener.Stop();
            var fixture = Path.Combine(root, "fixture.gguf");
            WriteFixture(fixture, false);
            var model = GgufInspector.Inspect(fixture);
            Check("Non-Qwen architecture from metadata", model.Architecture == "llama");
            Check("Native context metadata", model.NativeContext == 8192);
            Check("Unknown MTP is not guessed from filename", model.Mtp == "Unknown");
            var mtpFile = Path.Combine(root, "mtp.gguf");
            WriteFixture(mtpFile, true);
            var mtpModel = GgufInspector.Inspect(mtpFile);
            Check("MTP tensors and metadata detected", mtpModel.Mtp == "Supported");
            string pleFixture = Path.Combine(root, "learned-00001-of-00002.gguf");
            WritePleShard(pleFixture, 0, "futureqwen", true);
            WritePleShard(Path.Combine(root, "learned-00002-of-00002.gguf"), 1, "futureqwen", true);
            var learned = GgufInspector.Inspect(pleFixture);
            Check("Future architecture PLE detected without filename or architecture guessing", learned.Architecture == "futureqwen" && learned.HasPle && learned.Mtp == "Unknown");
            Check("Learned table parameters in later shards are counted separately", learned.Shards == 2 && learned.PleParameters == 128 && learned.Parameters == 256 && learned.CoreParameterBillions == 128 / 1e9);
            string otherEmbeddings = Path.Combine(root, "other-embeddings.gguf");
            WritePleShard(otherEmbeddings, 1, "other", false);
            var other = GgufInspector.Inspect(otherEmbeddings);
            Check("Per-layer embeddings alone do not imply learned n-grams", !other.HasPle && other.PleParameters == 0);
            var featureProbe = new EngineCapabilities("--reasoning-effort VALUE\n--spec-type [none|ngram-map-k4v]\n--draft-max N  the argument has been removed. use --spec-draft-n-max\n--spec-draft-n-max N\n--model-draft FILE\n");
            Check("Runtime flags require exact names", !featureProbe.HasFlag("--reasoning") && featureProbe.HasFlag("--reasoning-effort"));
            var preserveProbe = new EngineCapabilities("--reasoning-preserve, --no-reasoning-preserve\n    preserve reasoning trace\n");
            Check("Paired reasoning-preserve flags are both recognized", preserveProbe.HasFlag("--reasoning-preserve") && preserveProbe.HasFlag("--no-reasoning-preserve"));
            Check("N-gram support requires exact advertised mode", !featureProbe.HasSpecType("ngram-map-k") && featureProbe.HasSpecType("ngram-map-k4v"));
            Check("Removed flags cannot pass capability validation", !featureProbe.HasFlag("--draft-max") && featureProbe.RequiredFlag("--draft-max", "--spec-draft-n-max") == "--spec-draft-n-max");
            Check("Advertised draft-model alias is supported", featureProbe.RequiredFlag("--spec-draft-model", "--model-draft") == "--model-draft");
            var bad = Path.Combine(root, "bad.gguf");
            File.WriteAllText(bad, "not a model");
            bool rejected = false;
            try
            {
                GgufInspector.Inspect(bad);
            }
            catch { rejected = true; }
            Check("Invalid GGUF rejected", rejected);
            var p = new Profile { Path = fixture, Alias = "test", Name = "Fixture" };
            Check("New profiles use Balanced memory safety", p.MemorySafety == "Balanced");
            var h = new Hardware { RamGiB = 32, AvailableRamGiB = 24, Gpus = new() { new("Test GPU", "NVIDIA", 8, 7, false) } };
            var e1 = MemoryEstimator.Calculate(model, p, h, "CUDA");
            learned.Bytes = 30L * 1024 * 1024 * 1024;
            learned.Layers = 48;
            learned.KvHeads = 8;
            learned.KeyLength = learned.ValueLength = 128;
            var futureProfile = new Profile();
            string futureSettings = futureProfile.Options.ToJsonString();
            Check("Learned-table placement estimates stay uncertain", MemoryEstimator.Calculate(learned, futureProfile, h, "CUDA").Approximate);
            MemoryEstimator.Optimize(futureProfile, learned, h, "CUDA");
            Check("Unknown learned-table architecture retains its settings during Auto Optimize", futureProfile.Options.ToJsonString() == futureSettings);
            learned.Architecture = "qwen4exp";
            MemoryEstimator.Optimize(futureProfile, learned, h, "CUDA");
            Check("Future 27B qwen4exp cannot inherit the 125B Flash-Next optimization preset", !learned.UsesValidatedFlashNextPreset && futureProfile.Options.ToJsonString() == futureSettings);
            learned.Architecture = "futureqwen";
            futureProfile.Options["CpuMoe"] = true;
            var futureEstimate = MemoryEstimator.Calculate(learned, futureProfile, h, "CUDA");
            Check("Future learned tables cannot inherit small Flash-Next MoE weight fractions", futureEstimate.ModelVram >= learned.GiB && futureEstimate.Ram >= learned.GiB);
            Check("VRAM estimate exposes model, KV, and runtime components that sum to its total", Math.Abs(e1.Vram - e1.ModelVram - e1.KvVram - e1.RuntimeVram) < 0.000001);
            p.Options["ContextSize"] = 8192;
            var e2 = MemoryEstimator.Calculate(model, p, h, "CUDA");
            Check("Context changes KV memory", e2.Vram > e1.Vram);
            p.Options["CacheK"] = "Q8_0";
            p.Options["CacheV"] = "Q8_0";
            Check("KV quantization reduces estimate", MemoryEstimator.Calculate(model, p, h, "CUDA").Vram < e2.Vram);
            p.Options["GpuLayers"] = "0";
            Check("CPU offload moves weights to RAM", MemoryEstimator.Calculate(model, p, h, "CPU").Vram == 0);
            p.Options["GpuLayers"] = "ALL";
            p.Options["ContextSize"] = 1048576;
            p.MemorySafety = "Relaxed";
            var relaxedEstimate = MemoryEstimator.Calculate(model, p, h, "CUDA");
            p.MemorySafety = "Balanced";
            var balancedEstimate = MemoryEstimator.Calculate(model, p, h, "CUDA");
            p.MemorySafety = "Strict";
            var strictEstimate = MemoryEstimator.Calculate(model, p, h, "CUDA");
            Check("Named reserves are ordered Strict >= Balanced >= Relaxed", strictEstimate.VramReserve >= balancedEstimate.VramReserve && balancedEstimate.VramReserve >= relaxedEstimate.VramReserve && strictEstimate.RamReserve >= balancedEstimate.RamReserve && balancedEstimate.RamReserve >= relaxedEstimate.RamReserve);
            Check("Strict blocks a synthetic over-budget estimate", strictEstimate.OverBudget && MemoryPolicies.Admission(p, strictEstimate) == MemoryAdmission.Block);
            p.MemorySafety = "Balanced";
            Check("Balanced requires confirmation when synthetic estimate is over budget", MemoryPolicies.Admission(p, balancedEstimate) == MemoryAdmission.Confirm);
            p.MemorySafety = "Relaxed";
            Check("Relaxed requires confirmation when clearly over budget", MemoryPolicies.Admission(p, relaxedEstimate) == MemoryAdmission.Confirm);
            p.MemorySafety = "Off";
            var uncheckedEstimate = MemoryEstimator.Calculate(model, p, h, "CUDA");
            Check("Off bypasses launcher memory admission", uncheckedEstimate.OverBudget && MemoryPolicies.Admission(p, uncheckedEstimate) == MemoryAdmission.Allow);
            p.MemorySafety = "Custom";
            p.CustomVramReserveGiB = 3.5;
            p.CustomRamReserveGiB = 7.5;
            p.CustomOverBudgetAction = "Warn";
            Check("Custom policy retains explicit reserves and action", MemoryEstimator.Calculate(model, p, h, "CUDA").VramReserve == 3.5 && MemoryPolicies.Admission(p, MemoryEstimator.Calculate(model, p, h, "CUDA")) == MemoryAdmission.Warn);
            p.MemorySafety = "Balanced";
            p.Options["ContextSize"] = 8192;
            bool releaseTagParsed = EngineManager.TryBuildNumber("b11177", out int releaseBuild);
            bool serverVersionParsed = EngineManager.TryBuildNumber("version: 0.1.2-dev (build 10549, commit b2e5e9b28)", out int serverBuild);
            Check("Upstream release tag build number parses", releaseTagParsed && releaseBuild == 11177);
            Check("Installed server build metadata parses", serverVersionParsed && serverBuild == 10549);
            var engine = new EngineManager(AppContext.BaseDirectory, root);
            var probe = await engine.Inspect("CPU");
            Check("Bundled CPU runtime starts", probe.Version.Contains("10549"));
            p.Acceleration = "Off";
            p.Ngram = "Off";
            var plain = Arguments.Build(p, store.Value, model, "CPU", probe.Help);
            p.Options["ParallelSlots"] = 24;
            var parallelArgs = Arguments.Build(p, store.Value, model, "CPU", probe.Help);
            Check("llama.cpp accepts a parallel request count above 20", parallelArgs[parallelArgs.IndexOf("--parallel") + 1] == "24");
            p.Options["ParallelSlots"] = 1;
            p.Options["ContextCacheInRam"] = true;
            var ramArgs = Arguments.Build(p, store.Value, model, "CPU", probe.Help);
            Check("llama.cpp context cache can be placed in RAM", ramArgs.Contains("--no-kv-offload") && !plain.Contains("--no-kv-offload"));
            p.Options["ContextCacheInRam"] = false;
            Check("Generic model has no Qwen reasoning or projector", !plain.Contains("--reasoning") && !plain.Contains("--mmproj") && !plain.Contains("--chat-template-file"));
            p.Options["MtpDraftMax"] = 7;
            plain = Arguments.Build(p, store.Value, model, "CPU", probe.Help);
            Check("Inactive speculative values persist without conflicting flags", p.Options.I("MtpDraftMax") == 7 && !plain.Contains("--spec-type"));
            p.Ngram = "ngram-map-k";
            var ngram = Arguments.Build(p, store.Value, model, "CPU", probe.Help);
            Check("N-gram operates independently with MTP off", ngram.Contains(p.Ngram) && !ngram.Contains("draft-mtp"));
            var learnedProfile = new Profile { Path = pleFixture, Alias = "learned-test", Acceleration = "Off", Ngram = "ngram-mod" };
            var lazyArgs = Arguments.Build(learnedProfile, store.Value, learned, "CPU", probe.Help + "\n--tensor-read-lazy [on|off]\n");
            Check("Learned-table lazy request follows metadata and runtime capability", lazyArgs.Contains("--tensor-read-lazy") && lazyArgs.Contains("ngram-mod"));
            var defaultTableArgs = Arguments.Build(learnedProfile, store.Value, learned, "CPU", probe.Help);
            Check("Runtime without lazy control retains its own table-loading defaults", !defaultTableArgs.Contains("--tensor-read-lazy"));
            p.Acceleration = "On";
            rejected = false;
            try
            {
                Arguments.Build(p, store.Value, model, "CPU", probe.Help);
            }
            catch { rejected = true; }
            Check("Unknown MTP gated", rejected);
            var combinedProfile = new Profile { Path = mtpFile, Alias = "mtp-test", Acceleration = "On", Ngram = "ngram-map-k" };
            combinedProfile.Options["ContextSize"] = 8192;
            combinedProfile.Options["GpuLayers"] = "0";
            var combined = Arguments.Build(combinedProfile, store.Value, mtpModel, "CPU", probe.Help);
            int specIndex = combined.IndexOf("--spec-type");
            Check("MTP and N-gram compose into one speculative pipeline", specIndex >= 0 && combined[specIndex + 1] == "draft-mtp,ngram-map-k" && combined.Count(x => x == "--spec-type") == 1);
            Check("Draft verification stays greedy unless chosen", !combined.Contains("--spec-draft-sampling"));
            combinedProfile.Options["MtpDraftSampling"] = "probabilistic";
            var probabilistic = Arguments.Build(combinedProfile, store.Value, mtpModel, "CPU", probe.Help + "\n--spec-draft-sampling {greedy,probabilistic}\n");
            int samplingIndex = probabilistic.IndexOf("--spec-draft-sampling");
            Check("Probabilistic draft verification is emitted when the engine offers it", samplingIndex >= 0 && probabilistic[samplingIndex + 1] == "probabilistic");
            rejected = false;
            try { Arguments.Build(combinedProfile, store.Value, mtpModel, "CPU", probe.Help.Replace("--spec-draft-sampling", "")); }
            catch (ArgumentException) { rejected = true; }
            Check("Probabilistic draft verification is refused by an engine without it", rejected);
            combinedProfile.Options.Remove("MtpDraftSampling");
            var dflashFile = Path.Combine(root, "dflash-drafter.gguf");
            WriteFixture(dflashFile, false, "dflash");
            var dflashProfile = new Profile { Path = fixture, Alias = "dflash-test", Acceleration = "On", Ngram = "ngram-mod", DraftModel = dflashFile };
            dflashProfile.Options["ContextSize"] = 8192;
            dflashProfile.Options["GpuLayers"] = "0";
            // First, so its value list is the one the capability check reads (the bundled CPU fixture engine predates DFlash).
            var dflashHelp = "--spec-type none,draft-mtp,draft-dflash,ngram-mod\n--spec-draft-model FNAME\n" + probe.Help;
            var dflash = Arguments.Build(dflashProfile, store.Value, model, "CPU", dflashHelp);
            int dflashIndex = dflash.IndexOf("--spec-type");
            Check("A DFlash 2 drafter runs as draft-dflash, not as an MTP head", dflashIndex >= 0 && dflash[dflashIndex + 1] == "draft-dflash,ngram-mod" && dflash.Contains(dflashFile));
            var mtpHeadProfile = new Profile { Path = fixture, Alias = "mtp-head-test", Acceleration = "On", Ngram = "Off", DraftModel = mtpFile };
            mtpHeadProfile.Options["ContextSize"] = 8192;
            mtpHeadProfile.Options["GpuLayers"] = "0";
            var mtpHead = Arguments.Build(mtpHeadProfile, store.Value, model, "CPU", dflashHelp);
            Check("A separate MTP head still runs as draft-mtp", mtpHead[mtpHead.IndexOf("--spec-type") + 1] == "draft-mtp");
            // This process stands in for an open Qwen Code Desktop: it started before now and long after a year ago.
            Check("A Qwen Code Desktop window opened before a sync is detected, and none is invented",
                Integration.DesktopOpenedBefore(Environment.ProcessPath!, DateTime.Now.AddSeconds(1))
                && !Integration.DesktopOpenedBefore(Environment.ProcessPath!, DateTime.Now.AddYears(-1))
                && !Integration.DesktopOpenedBefore("", DateTime.Now));
            if (Directory.Exists(engine.ExperimentalRoot))
            {
                var tq = new Profile
                {
                    Path = fixture,
                    Alias = "turboquant-test",
                    RuntimeId = "TurboQuant / Flash-Next (Experimental)",
                    TurboQuantEnabled = true,
                    TurboQuantPreset = "Quality-first"
                };
                tq.Options["ContextSize"] = 8192;
                tq.Options["CacheK"] = "F16";
                tq.Options["CacheV"] = "BF16";
                tq.Options["GpuLayers"] = "ALL";
                tq.Options["CpuMoe"] = true;
                store.Value.ShowTurboQuantControls = true;
                var tqProbe = await engine.Inspect("CUDA", tq);
                var tqArgs = Arguments.Build(tq, store.Value, model, "CUDA", tqProbe.Help);
                int tqK = tqArgs.IndexOf("--cache-type-k"), tqV = tqArgs.IndexOf("--cache-type-v");
                Check("Quality-first TurboQuant emits native Q8 K and Turbo4 V", tqK >= 0 && tqArgs[tqK + 1] == "q8_0" && tqV >= 0 && tqArgs[tqV + 1] == "turbo4");
                var longContextModel = new ModelInfo { Architecture = "qwen35", Layers = 64, KvHeads = 8, KeyLength = 128, ValueLength = 128, FullAttentionInterval = 4, NativeContext = 262144, Bytes = 17L * 1024 * 1024 * 1024 };
                var longContextHardware = new Hardware { RamGiB = 96, AvailableRamGiB = 78, Gpus = new() { new("RTX 5090", "NVIDIA", 31.8, 30.3, false) } };
                tq.Options["ContextSize"] = 262144;
                tq.TurboQuantPreset = "Balanced";
                var balancedCache = Arguments.EffectiveCache(tq, longContextModel);
                double balancedVram = MemoryEstimator.Calculate(longContextModel, tq, longContextHardware, "CUDA").Vram;
                tq.TurboQuantPreset = "Memory saver";
                var saverCache = Arguments.EffectiveCache(tq, longContextModel);
                double saverVram = MemoryEstimator.Calculate(longContextModel, tq, longContextHardware, "CUDA").Vram;
                Check("Memory saver lowers V precision and estimates below Balanced on ordinary models", balancedCache.K == "Turbo4" && saverCache.K == "Turbo4" && saverCache.V == "Turbo3" && saverVram < balancedVram);
                var saverArgs = Arguments.Build(tq, store.Value, longContextModel, "CUDA", tqProbe.Help);
                tqK = saverArgs.IndexOf("--cache-type-k"); tqV = saverArgs.IndexOf("--cache-type-v");
                Check("Memory saver emits the codecs used by the estimate", saverArgs[tqK + 1] == "turbo4" && saverArgs[tqV + 1] == "turbo3");
                var optimized27B = new Profile();
                MemoryEstimator.Optimize(optimized27B, longContextModel, longContextHardware, "CUDA");
                Check("Auto Optimize raises 27B CUDA batches above the starter defaults", optimized27B.Options.I("BatchSize") == 4096 && optimized27B.Options.I("UBatchSize") == 1024);
                var tightHardware = new Hardware { RamGiB = 96, AvailableRamGiB = 78, Gpus = new() { new("Tight GPU", "NVIDIA", 20, 20, false) } };
                var optimizedTight = new Profile();
                MemoryEstimator.Optimize(optimizedTight, longContextModel, tightHardware, "CUDA");
                Check("Auto Optimize keeps small batches when GPU headroom is tight", optimizedTight.Options.I("BatchSize") == 512 && optimizedTight.Options.I("UBatchSize") == 128);
                Check("Explicit CPU-MoE placement remains CUDA-backed", tqArgs.Contains("CUDA0") && tqArgs.Contains("--cpu-moe") && tqArgs.Contains("--moe-cache"));
                var flashInfo = new ModelInfo { Architecture = "qwen4exp", Layers = 48, Reasoning = true, Mtp = "Unknown", Bytes = 111_334_654_784L, HasPle = true };
                var flashSaver = Arguments.EffectiveCache(tq, flashInfo);
                Check("Flash-Next Memory saver retains required Q8 indexer keys", flashSaver.K == "Q8_0" && flashSaver.V == "Turbo3");
                var flashMtp = new Profile { Path = fixture, DraftModel = mtpFile, Acceleration = "On", RuntimeId = EngineManager.FlashNextRuntime };
                flashMtp.Options["ContextSize"] = 65536;
                flashMtp.Options["GpuLayers"] = "ALL";
                flashMtp.Options["CpuMoe"] = true;
                flashMtp.Options["CpuMoeLayers"] = 36;
                flashMtp.Options["MoeCacheEnabled"] = false;
                var flashMtpArgs = Arguments.Build(flashMtp, store.Value, flashInfo, "CUDA", tqProbe.Help);
                int draftCtxIndex = flashMtpArgs.IndexOf("--ctx-size-draft");
                Check("MTP draft context follows the selected target context", draftCtxIndex >= 0 && flashMtpArgs[draftCtxIndex + 1] == "65536");
                int cpuMoeIndex = flashMtpArgs.IndexOf("--n-cpu-moe");
                int moeCacheIndex = flashMtpArgs.IndexOf("--moe-cache");
                Check("Flash-Next uses permanent partial expert offload instead of all-CPU placement", cpuMoeIndex >= 0 && flashMtpArgs[cpuMoeIndex + 1] == "36" && !flashMtpArgs.Contains("--cpu-moe"));
                Check("Flash-Next optimized placement leaves the prompt-bypassed dynamic cache off", moeCacheIndex >= 0 && flashMtpArgs[moeCacheIndex + 1] == "off");
                var migratedFlash = new Profile { RuntimeId = EngineManager.BundledRuntime };
                Check("Existing Flash-Next profiles auto-route away from the incompatible bundled runtime", engine.EnsureCompatibleProfile(migratedFlash, flashInfo) && migratedFlash.RuntimeId == EngineManager.FlashNextRuntime);
                var polyFlash = new Profile { RuntimeId = PolyStrata.Runtime, PolyStrataConfig = "saved-poly-config", DraftModel = "saved-draft" };
                Check("Flash-Next compatibility repair preserves the selected PolyStrata runtime and settings",
                    !engine.EnsureCompatibleProfile(polyFlash, flashInfo) && polyFlash.RuntimeId == PolyStrata.Runtime
                    && polyFlash.PolyStrataConfig == "saved-poly-config" && polyFlash.DraftModel == "saved-draft");
                var futureQwen4 = new ModelInfo { Architecture = "qwen4exp", Layers = 64, Reasoning = true, Mtp = "Supported", Bytes = 17_500_000_000L, HasPle = true };
                var futureQwen4Profile = new Profile { RuntimeId = EngineManager.BundledRuntime };
                bool bundledKnowsQwen4exp = engine.BundledSupportsArchitecture("qwen4exp");
                Check("A new qwen4exp model follows an upstream engine that implements it, and is pinned only when it does not",
                    engine.EnsureCompatibleProfile(futureQwen4Profile, futureQwen4) != bundledKnowsQwen4exp
                    && futureQwen4Profile.RuntimeId == (bundledKnowsQwen4exp ? EngineManager.BundledRuntime : EngineManager.FlashNextRuntime));
                Check("Architecture detection matches exact names only", !engine.BundledSupportsArchitecture("qwen4") && !engine.BundledSupportsArchitecture("wen4exp"));
                var optimizedFlash = new Profile();
                var roomyFlash = new ModelInfo { Architecture = "qwen4exp", NativeContext = 262144, Layers = 48, KvHeads = 2, KeyLength = 256, ValueLength = 256, FullAttentionInterval = 4, Bytes = 111_334_654_784L, HasPle = true };
                var roomyHardware = new Hardware { RamGiB = 96, AvailableRamGiB = 78, Gpus = new() { new("RTX 5090", "NVIDIA", 31.8, 30, false) } };
                MemoryEstimator.Optimize(optimizedFlash, roomyFlash, roomyHardware, "CUDA");
                Check("Flash-Next Auto Optimize starts at the validated 64K context", optimizedFlash.Options.I("ContextSize") == 65536);
                Check("Flash-Next Auto Optimize applies the measured 96 GB hybrid placement", optimizedFlash.Options.I("CpuMoeLayers") == 36 && !optimizedFlash.Options.B("MoeCacheEnabled") && optimizedFlash.Options.I("WorkingSetLimitGiB") == 65 && optimizedFlash.Options.I("UBatchSize") == 512);
                tq.TurboQuantPreset = "Balanced";
                rejected = false;
                try { Arguments.Build(tq, store.Value, flashInfo, "CUDA", tqProbe.Help); }
                catch (ArgumentException ex) when (ex.Message.Contains("indexer K", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
                Check("Flash-Next rejects unsupported TurboQuant key compression", rejected);
                tq.TurboQuantPreset = "Quality-first";
                tq.Options["Thinking"] = true;
                var reasoningArgs = Arguments.Build(tq, store.Value, flashInfo, "CUDA", tqProbe.Help);
                Check("Runtime without global reasoning-effort flag still starts Thinking", reasoningArgs.Contains("--reasoning") && !reasoningArgs.Contains("--reasoning-effort"));
                int lazyIndex = reasoningArgs.IndexOf("--tensor-read-lazy");
                Check("Flash-Next keeps its learned PLE N-gram table SSD-backed", lazyIndex >= 0 && reasoningArgs[lazyIndex + 1] == "on");
                int promptCacheIndex = reasoningArgs.IndexOf("--cache-ram");
                Check("Flash-Next retains one complete Qwen Code prefix without inheriting 8 GiB", promptCacheIndex >= 0 && reasoningArgs[promptCacheIndex + 1] == "2048");
                tq.Acceleration = "Force MTP (Experimental)";
                rejected = false;
                try { Arguments.Build(tq, store.Value, flashInfo, "CUDA", tqProbe.Help); }
                catch (ArgumentException ex) when (ex.Message.Contains("separate MTP-head", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
                Check("Flash-Next Force MTP without a head is rejected before model allocation", rejected);
                tq.Acceleration = "Auto";
                tq.Options["Thinking"] = false;
                tq.TurboQuantEnabled = false;
                var ordinaryArgs = Arguments.Build(tq, store.Value, model, "CUDA", tqProbe.Help);
                tqK = ordinaryArgs.IndexOf("--cache-type-k"); tqV = ordinaryArgs.IndexOf("--cache-type-v");
                Check("Turning TurboQuant off restores preserved ordinary cache values", ordinaryArgs[tqK + 1] == "f16" && ordinaryArgs[tqV + 1] == "bf16");
                store.Value.ShowTurboQuantControls = false;
            }
            p.Acceleration = "Off";
            p.Ngram = "Off";
            p.CustomArguments = "--port 9999";
            rejected = false;
            try
            {
                Arguments.Build(p, store.Value, model, "CPU", probe.Help);
            }
            catch { rejected = true; }
            Check("Custom arguments cannot override managed network", rejected);
            p.CustomArguments = "";
            store.Value.Models.Add(p);
            store.Value.ActiveId = p.Id;
            store.Save();
            Check("Per-model profile persists", new ConfigStore(root, false).Value.Models[0].Options.I("ContextSize") == 8192);
            bool cacheRoundTrips = true;
            foreach (string cacheType in MainForm.SupportedCacheTypes)
            {
                p.Options["CacheK"] = cacheType;
                p.Options["CacheV"] = cacheType;
                store.Save();
                var cached = new ConfigStore(root, false).Value.Models[0];
                cacheRoundTrips &= cached.Options.S("CacheK") == cacheType && cached.Options.S("CacheV") == cacheType;
            }
            Check("Every K/V slider and dropdown type round-trips", cacheRoundTrips);
            p.Options["ContextSize"] = 12345;
            p.CustomVramReserveGiB = 4.5;
            p.CustomRamReserveGiB = 9.5;
            p.MemorySafety = "Balanced";
            store.Save();
            var roundTrip = new ConfigStore(root, false).Value.Models[0];
            Check("Exact custom context and hidden Custom values survive reload", roundTrip.Options.I("ContextSize") == 12345 && roundTrip.CustomVramReserveGiB == 4.5 && roundTrip.CustomRamReserveGiB == 9.5);
            var oldJson = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
            oldJson["Models"]![0]!.AsObject().Remove("MemorySafety");
            File.WriteAllText(store.FilePath, oldJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Check("Pre-policy profiles migrate to behavior-equivalent Off", new ConfigStore(root, false).Value.Models[0].MemorySafety == "Off");
            p.Options["ContextSize"] = 8192;
            store.Save();
            File.WriteAllText(Path.Combine(root, "settings.json"), "broken");
            Check("Invalid settings recover from backup", new ConfigStore(root, false).Notice.Contains("Recovered"));
            using var manager = new RuntimeManager(store);
            string report = manager.Report(h, model, p, "CPU", probe.Version);
            Check("Diagnostics redact key and model path", !report.Contains(store.Value.ApiKey) && !report.Contains(fixture));
            var brokenBundle = Path.Combine(root, "broken-engine");
            Directory.CreateDirectory(brokenBundle);
            File.WriteAllText(Path.Combine(brokenBundle, "manifest.json"), "{\"../escape.exe\":\"bad\"}");
            var before = engine.Root;
            rejected = false;
            try
            {
                await engine.Stage(brokenBundle, "CPU");
            }
            catch { rejected = true; }
            Check("Failed staged update retains active runtime", rejected && engine.Root == before);
            int splitIndex = Array.IndexOf(args, "--split-model");
            if (splitIndex >= 0)
            {
                if (splitIndex + 1 >= args.Length)
                    throw new ArgumentException("--split-model requires the first GGUF shard path.");
                string firstShard = args[splitIndex + 1];
                var split = GgufInspector.Inspect(firstShard);
                var prefix = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(firstShard), @"00001-of-\d{5}\.gguf$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var shards = Directory.GetFiles(Path.GetDirectoryName(firstShard)!, prefix + "*.gguf");
                Check("Real split GGUF recognizes every shard and sums complete model bytes", split.Shards == shards.Length && split.Bytes == shards.Sum(x => new FileInfo(x).Length));
                var splitProfile = new Profile { Path = firstShard };
                splitProfile.Options["ContextSize"] = 65536;
                var splitHardware = new Hardware { RamGiB = 256, AvailableRamGiB = 200, Gpus = new() { new("Test GPU", "NVIDIA", 160, 150, false) } };
                var splitEstimate = MemoryEstimator.Calculate(split, splitProfile, splitHardware, "CUDA");
                Check("Real split-model fit calculation includes all shard weights plus KV and runtime overhead", splitEstimate.ModelVram >= split.GiB && splitEstimate.Vram >= splitEstimate.ModelVram + splitEstimate.KvVram);
            }
            if (args.Contains("--legacy"))
            {
                store.Import(@"C:\AI\Models\Qwen3.8-Local.json");
                var legacy = JsonNode.Parse(File.ReadAllText(@"C:\AI\Models\Qwen3.8-Local.json"))!.AsObject();
                var selected = store.Value.Models.Single(x => x.Id == store.Value.ActiveId);
                Check("All existing profiles imported", store.Value.Models.Count == legacy["Models"]!.AsArray().Count);
                Check("Current Qwen settings retained", selected.Options.I("ContextSize") == legacy.I("ContextSize") && selected.Options.S("CacheK") == legacy.S("CacheK") && selected.Alias == legacy.S("ModelAlias") && store.Value.ApiKey == legacy.S("ApiKey") && store.Value.Lan == legacy.B("LanAccess"));
                var metadata = GgufInspector.Inspect(selected.Path);
                File.WriteAllText(Path.Combine(output, "qwen-metadata.json"), JsonSerializer.Serialize(new
                {
                    metadata.Architecture,
                    metadata.Parameters,
                    metadata.Mtp,
                    metadata.MtpEvidence,
                    metadata.NativeContext,
                    metadata.Layers,
                    metadata.KvHeads,
                    metadata.KeyLength,
                    metadata.ValueLength,
                    metadata.FullAttentionInterval,
                    Keys = metadata.Metadata.Keys.Where(k => !k.StartsWith("tokenizer.")).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
                var cuda = await engine.Inspect("CUDA");
                var actual = Arguments.Build(selected, store.Value, metadata, "CUDA", cuda.Help);
                File.WriteAllText(Path.Combine(output, "new-arguments.json"), JsonSerializer.Serialize(actual.Select((x, i) => i > 0 && actual[i - 1] == "--api-key" ? "<redacted>" : x)));
                var expectedPath = Path.Combine(output, "baseline-arguments.json");
                if (File.Exists(expectedPath))
                {
                    var expected = JsonSerializer.Deserialize<string[]>(File.ReadAllText(expectedPath))!;
                    Check("Exact argument parity with original Qwen builder", expected.SequenceEqual(actual.Select((x, i) => i > 0 && actual[i - 1] == "--api-key" ? "<redacted>" : x)));
                }
                Check("Known-good MTP preset retained", selected.KnownGood?.B("MtpEnabled") == true);
                var syncRoot = Path.Combine(root, "sync-fixture");
                Directory.CreateDirectory(syncRoot);
                var syncConfig = Integration.QwenConfiguration(store.Value, selected, engine.Executable("CUDA"));
                var syncSettings = Path.Combine(syncRoot, "settings.json");
                syncConfig["QwenSettings"] = syncSettings;
                File.WriteAllText(syncSettings, "{\"fixturePreserved\":true}");
                File.WriteAllText(Path.Combine(syncRoot, "config.json"), syncConfig.ToJsonString());
                File.WriteAllText(Path.Combine(syncRoot, "Qwen3.8-Reasoning.ps1"), "function Repair-Qwen38ReasoningRuntime { param($Config) }");
                string Q(string v) => "'" + v.Replace("'", "''") + "'";
                string syncScript = "$ErrorActionPreference='Stop'; . " + Q(store.Value.LegacyCommon) + "; function Repair-QwenCodeCompressionRuntime { }; $script:Qwen38Root=" + Q(syncRoot) + "; $c=Get-Content -LiteralPath " + Q(Path.Combine(syncRoot, "config.json")) + " -Raw | ConvertFrom-Json; if ([IO.Path]::GetFullPath([string]$c.QwenSettings) -ne [IO.Path]::GetFullPath(" + Q(syncSettings) + ")) { throw 'Test synchronization target escaped the isolated fixture.' }; Sync-QwenCodeSettings -Config $c";
                await EngineManager.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(syncScript)));
                var synced = JsonNode.Parse(File.ReadAllText(syncSettings))!.AsObject();
                Check("Original Qwen sync runs against isolated fixture", synced.B("fixturePreserved") && synced["model"]!["name"]!.ToString() == selected.Alias && synced["env"]!["LOCAL_QWEN38_API_KEY"]!.ToString() == store.Value.ApiKey);
                Check("Qwen sync preserves localhost endpoint and context", synced["model"]!["baseUrl"]!.ToString() == $"http://127.0.0.1:{store.Value.Port}/v1" && synced["modelProviders"]!["openai"]![0]!["generationConfig"]!["contextWindowSize"]!.GetValue<int>() == selected.Options.I("ContextSize"));
                var nonLegacy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(selected))!;
                nonLegacy.Legacy = false;
                nonLegacy.Acceleration = "On";
                nonLegacy.DraftModel = mtpFile;
                foreach (string key in new[] { "Temperature", "TopP", "TopK", "MinP", "PresencePenalty", "RepeatPenalty", "SamplingProfile" })
                    nonLegacy.Options.Remove(key);
                var nonLegacyConfig = Integration.QwenConfiguration(store.Value, nonLegacy, engine.Executable("CUDA"));
                nonLegacyConfig["QwenSettings"] = syncSettings;
                File.WriteAllText(Path.Combine(syncRoot, "config.json"), nonLegacyConfig.ToJsonString());
                await EngineManager.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(syncScript)));
                var repairedSync = JsonNode.Parse(File.ReadAllText(syncSettings))!.AsObject();
                Check("Sparse older profile passes the real Qwen synchronization validator", repairedSync["modelProviders"]!["openai"]![0]!["generationConfig"]!["samplingParams"]!["temperature"]!.GetValue<double>() == 0.7);
                Check("Non-legacy Flash-Next profiles synchronize as the active Qwen Code model", nonLegacyConfig.B("MtpEnabled") && nonLegacyConfig.S("ModelAlias") == nonLegacy.Alias);
                var liveConfig = Integration.QwenConfiguration(store.Value, nonLegacy, engine.Executable("CUDA"), "verified-live-model", 196608);
                liveConfig["QwenSettings"] = syncSettings;
                File.WriteAllText(Path.Combine(syncRoot, "config.json"), liveConfig.ToJsonString());
                await EngineManager.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(syncScript)));
                var liveSync = JsonNode.Parse(File.ReadAllText(syncSettings))!.AsObject();
                Check("Qwen bridge uses verified live model ID and 196K context", liveSync["model"]!["name"]!.ToString() == "verified-live-model" && liveSync["modelProviders"]!["openai"]![0]!["generationConfig"]!["contextWindowSize"]!.GetValue<int>() == 196608);
                string fingerprintBefore = Integration.Fingerprint(store.Value, nonLegacy);
                nonLegacy.Ngram = "ngram-mod";
                Check("Qwen synchronization fingerprint includes speculative profile changes", fingerprintBefore != Integration.Fingerprint(store.Value, nonLegacy));
                var thinkingCopy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(selected))!;
                thinkingCopy.SetThinking(true);
                Check("Qwen thinking toggle retains original sampling contract", thinkingCopy.Options.S("Temperature") == "1" && thinkingCopy.Options.B("PreserveThinking"));
                thinkingCopy.SetThinking(false);
                Check("Qwen nonthinking toggle retains original sampling contract", thinkingCopy.Options.S("PresencePenalty") == "1.5" && !thinkingCopy.Options.B("PreserveThinking"));
                if (args.Contains("--inference"))
                {
                    await manager.Start(selected, metadata, "CUDA", engine, cuda);
                    Check("Current Qwen starts with new process manager", manager.State == "Running");
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Value.ApiKey);
                    Check("Localhost model ID responds", (await http.GetStringAsync($"http://127.0.0.1:{store.Value.Port}/v1/models")).Contains(selected.Alias));
                    Check("Current LAN address responds", (await http.GetStringAsync($"http://{Integration.LanAddress()}:{store.Value.Port}/v1/models")).Contains(selected.Alias));
                    var payload = JsonSerializer.Serialize(new
                    {
                        model = selected.Alias,
                        messages = new[] { new { role = "user", content = "Reply OK." } },
                        max_tokens = 8,
                        temperature = 0,
                        stream = false
                    });
                    using var response = await http.PostAsync($"http://127.0.0.1:{store.Value.Port}/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));
                    Check("OpenAI chat completion works", response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("choices"));
                    await manager.Stop();
                    Check("Stop unloads owned server", manager.State == "Stopped" && !manager.Active);
                    selected.Acceleration = "On";
                    await manager.Start(selected, metadata, "CUDA", engine, cuda);
                    Check("Known-good Qwen MTP starts on restart", manager.State == "Running");
                    await manager.Stop();
                    selected.Acceleration = "Off";
                    selected.Ngram = "ngram-map-k";
                    selected.Options["ContextSize"] = 4096;
                    await manager.Start(selected, metadata, "CUDA", engine, cuda);
                    Check("N-gram model startup", manager.State == "Running");
                    await manager.Stop();
                }
            }
            if (args.Contains("--generic"))
            {
                var genericStore = new ConfigStore(Path.Combine(root, "generic"), false);
                genericStore.Value.Port = 18087;
                var generic = new Profile { Path = Path.Combine(output, "smollm2-test.gguf"), Alias = "smollm2-acceptance", Name = "Acceptance model" };
                generic.Options["ContextSize"] = 2048;
                generic.Options["GpuLayers"] = "0";
                generic.Acceleration = "Off";
                var gm = GgufInspector.Inspect(generic.Path);
                Check("Real non-Qwen model inspected", gm.Architecture == "llama" && gm.Mtp != "Supported");
                genericStore.Value.Models.Add(generic);
                genericStore.Save();
                using var runner = new RuntimeManager(genericStore);
                await runner.Start(generic, gm, "CPU", engine, probe);
                Check("Non-Qwen CPU model starts", runner.State == "Running");
                Check("Owned server verifies its live model ID and effective context", runner.LiveModelId == generic.Alias && runner.LiveContext == 2048);
                int? runningPid = runner.ProcessId;
                using (var observer = new RuntimeManager(genericStore))
                {
                    Check("Reopened launcher recovers the live model and context without launching another process", await observer.ReconcileExisting(genericStore.Value.Models) && observer.ExistingServer && observer.LiveProfile == generic && observer.LiveModelId == generic.Alias && observer.LiveContext == 2048 && runner.ProcessId == runningPid);
                    var wrongSelection = new Profile { Alias = "not-the-running-model", Path = generic.Path };
                    Check("Other model on occupied port is identified before a duplicate launch", await observer.ReconcileExisting(wrongSelection) && observer.ExistingServer && observer.State == "Different server on port");
                }
                var wrongKeyStore = new ConfigStore(genericStore.Root, false);
                wrongKeyStore.Value.ApiKey = "deliberately-wrong-test-key";
                using (var wrongKeyObserver = new RuntimeManager(wrongKeyStore))
                    Check("Reopen distinguishes an API key mismatch on the occupied port", await wrongKeyObserver.ReconcileExisting(generic) && wrongKeyObserver.ExistingServer && wrongKeyObserver.State == "API key mismatch");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", genericStore.Value.ApiKey);
                var body = JsonSerializer.Serialize(new
                {
                    model = generic.Alias,
                    messages = new[] { new { role = "user", content = "Hello" } },
                    max_tokens = 12
                });
                using var reply = await http.PostAsync("http://127.0.0.1:18087/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
                Check("Non-Qwen CPU chat response", reply.IsSuccessStatusCode);
                await runner.Stop();
                generic.Acceleration = "Off";
                generic.Ngram = "ngram-map-k";
                await runner.Start(generic, gm, "CPU", engine, probe);
                using var nreply = await http.PostAsync("http://127.0.0.1:18087/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
                Check("N-gram CPU inference", nreply.IsSuccessStatusCode);
                await runner.Stop();
                generic.Ngram = "ngram-mod";
                await runner.Start(generic, gm, "CPU", engine, probe);
                using var modReply = await http.PostAsync("http://127.0.0.1:18087/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
                string modResponse = await modReply.Content.ReadAsStringAsync();
                using var modJson = JsonDocument.Parse(modResponse);
                Check("N-gram mod produces a real nonempty CPU answer", modReply.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(modJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()));
                await runner.Stop();
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 18087);
                listener.Start();
                rejected = false;
                try
                {
                    await runner.Start(generic, gm, "CPU", engine, probe);
                }
                catch { rejected = true; }
                Check("Occupied unrelated port preserved", rejected && listener.Server.IsBound);
                listener.Stop();
                string profileBefore = generic.Options.ToJsonString();
                await runner.Start(generic, gm, "CPU", engine, probe, true);
                Check("Safe launch preserves profile", generic.Options.ToJsonString() == profileBefore && runner.State == "Running");
                await runner.Stop();
                var originalPath = generic.Path;
                generic.Path = fixture;
                rejected = false;
                try
                {
                    await runner.Start(generic, model, "CPU", engine, probe);
                }
                catch { rejected = true; }
                Check("Model startup failure records recovery state", rejected && runner.State == "Crashed" && genericStore.Value.StartupFailed);
                generic.Path = originalPath;
                var bundle = Path.Combine(root, "valid-bundle");
                Directory.CreateDirectory(Path.Combine(bundle, "cpu"));
                var files = new Dictionary<string, string>();
                foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "runtime", "bundled", "cpu")))
                {
                    var rel = "cpu/" + Path.GetFileName(file);
                    File.Copy(file, Path.Combine(bundle, "cpu", Path.GetFileName(file)));
                    files[rel] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
                }
                File.WriteAllText(Path.Combine(bundle, "manifest.json"), JsonSerializer.Serialize(files));
                var originalEngine = engine.Root;
                await engine.Stage(bundle, "CPU");
                Check("Verified update staged with previous engine retained", engine.Root != originalEngine && engine.CanRollback);
                await engine.Rollback("CPU");
                Check("Engine rollback restores original", engine.Root == originalEngine);
                var hostRoot = Path.Combine(root, "ownership");
                Directory.CreateDirectory(hostRoot);
                var hostStart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                hostStart.ArgumentList.Add("--ownership-host");
                hostStart.ArgumentList.Add(generic.Path);
                hostStart.ArgumentList.Add(hostRoot);
                using var host = System.Diagnostics.Process.Start(hostStart)!;
                try
                {
                    for (int i = 0; i < 60 && !File.Exists(Path.Combine(hostRoot, "server.pid")); i++)
                        await Task.Delay(500);
                    var ownedId = int.Parse(File.ReadAllText(Path.Combine(hostRoot, "server.pid")));
                    using var owned = System.Diagnostics.Process.GetProcessById(ownedId);
                    host.Kill();
                    await host.WaitForExitAsync();
                    Check("Abrupt launcher exit leaves no owned server", owned.WaitForExit(10000));
                }
                finally { if (!host.HasExited) host.Kill(); }
                var detachRoot = Path.Combine(root, "detached");
                Directory.CreateDirectory(detachRoot);
                var detachStart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                detachStart.ArgumentList.Add("--detach-host");
                detachStart.ArgumentList.Add(generic.Path);
                detachStart.ArgumentList.Add(detachRoot);
                using var detachHost = System.Diagnostics.Process.Start(detachStart)!;
                System.Diagnostics.Process? detached = null;
                try
                {
                    await detachHost.WaitForExitAsync();
                    string pidFile = Path.Combine(detachRoot, "detached-server.pid");
                    Check("Graceful launcher close detaches the ready server", detachHost.ExitCode == 0 && File.Exists(pidFile));
                    detached = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
                    var recoveredStore = new ConfigStore(detachRoot, false);
                    recoveredStore.Value.DetachedServer = null; // Simulate an already-running server left by the previous release.
                    recoveredStore.Save();
                    using var reopened = new RuntimeManager(recoveredStore, engine);
                    Check("Reopened launcher reattaches to the same server after its parent exited",
                        !detached.HasExited && await reopened.ReconcileExisting(recoveredStore.Value.Models)
                        && reopened.LiveModelId == "detached-test" && reopened.LiveContext == 2048
                        && reopened.LiveProfile?.Path == generic.Path && reopened.Active
                        && !reopened.ExistingServer && reopened.State == "Running · Live"
                        && reopened.ProcessId == detached.Id);
                    using var detachedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    detachedHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", recoveredStore.Value.ApiKey);
                    using var detachedReply = await detachedHttp.GetAsync("http://127.0.0.1:18090/v1/models");
                    Check("Detached server remains responsive after the launcher exits", detachedReply.IsSuccessStatusCode);
                    Check("A reconnected server keeps a current reconnect record", new ConfigStore(detachRoot, false).Value.DetachedServer?.ProcessId == detached.Id);
                    reopened.Dispose(); // The reopened launcher ends abruptly (crash, Task Manager): no Detach runs.
                    using var afterCrash = new RuntimeManager(new ConfigStore(detachRoot, false));
                    Check("A launcher ended abruptly after reconnecting leaves the next one able to reconnect",
                        !detached.HasExited && await afterCrash.ReconcileExisting(recoveredStore.Value.Models)
                        && afterCrash.Active && afterCrash.ProcessId == detached.Id);
                    afterCrash.Detach();
                    using var nextOpen = new RuntimeManager(new ConfigStore(detachRoot, false));
                    Check("Second reopen restores Stop and Restart control of the same PID",
                        await nextOpen.ReconcileExisting(recoveredStore.Value.Models)
                        && nextOpen.Active && nextOpen.ProcessId == detached.Id);
                    await nextOpen.Stop();
                    Check("Reattached Stop unloads the server", detached.WaitForExit(10000));
                    Check("Stop clears the reconnect record", new ConfigStore(detachRoot, false).Value.DetachedServer == null);
                }
                finally
                {
                    if (!detachHost.HasExited) detachHost.Kill();
                    if (detached is { HasExited: false })
                    {
                        detached.Kill(true);
                        await detached.WaitForExitAsync();
                    }
                    detached?.Dispose();
                }
            }
            if (args.Contains("--generic"))
            {
                var externalRoot = Path.Combine(root, "external");
                Directory.CreateDirectory(externalRoot);
                var externalStart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                externalStart.ArgumentList.Add("--detach-host");
                externalStart.ArgumentList.Add(Path.Combine(output, "smollm2-test.gguf"));
                externalStart.ArgumentList.Add(externalRoot);
                using (var externalHost = System.Diagnostics.Process.Start(externalStart)!)
                    await externalHost.WaitForExitAsync();
                using var externalServer = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(externalRoot, "detached-server.pid"))));
                try
                {
                    var externalStore = new ConfigStore(externalRoot, false);
                    externalStore.Value.DetachedServer = null; // Started by a script or another tool: nothing to reconnect by.
                    externalStore.Save();
                    using var external = new RuntimeManager(externalStore);
                    Check("A registered model's server that the launcher did not start is offered to Stop, not adopted",
                        await external.ReconcileExisting(externalStore.Value.Models) && !external.Active && external.ExistingServer
                        && external.State == "Existing server running" && external.CanStopExisting);
                    await external.StopExisting();
                    Check("Confirmed Stop ends a server the launcher did not start and frees its port",
                        externalServer.WaitForExit(10000) && external.State == "Stopped" && !external.ExistingServer && !external.CanStopExisting
                        && !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 18090));
                }
                finally
                {
                    if (!externalServer.HasExited)
                    {
                        externalServer.Kill(true);
                        await externalServer.WaitForExitAsync();
                    }
                }
            }
            int contextModelIndex = Array.IndexOf(args, "--context-model");
            if (contextModelIndex >= 0)
            {
                if (contextModelIndex + 1 >= args.Length)
                    throw new ArgumentException("--context-model requires a GGUF path.");
                string contextModelPath = args[contextModelIndex + 1];
                var contextRoot = Path.Combine(root, "context-validation");
                var contextStore = new ConfigStore(contextRoot, false);
                contextStore.Value.Port = 18091;
                contextStore.Value.Backend = "CUDA";
                contextStore.Value.ShowTurboQuantControls = true;
                var contextProfile = new Profile
                {
                    Path = contextModelPath,
                    Name = "Real context validation",
                    Alias = "context-validation",
                    RuntimeId = EngineManager.FlashNextRuntime,
                    Acceleration = "Off",
                    TurboQuantEnabled = true,
                    TurboQuantPreset = "Balanced"
                };
                contextProfile.Options["GpuLayers"] = "ALL";
                contextProfile.Options["CacheK"] = "Q8_0";
                contextProfile.Options["CacheV"] = "Q8_0";
                contextProfile.Options["FlashAttention"] = true;
                contextProfile.Options["BatchSize"] = 4096;
                contextProfile.Options["UBatchSize"] = 1024;
                contextStore.Value.Models.Add(contextProfile);
                contextStore.Value.ActiveId = contextProfile.Id;
                var contextModel = GgufInspector.Inspect(contextModelPath);
                var contextEngine = new EngineManager(AppContext.BaseDirectory, contextRoot);
                string contextBackend = await contextEngine.Resolve("CUDA", Hardware.Detect(), contextProfile);
                Check("Real context validation resolves CUDA", contextBackend == "CUDA");
                var contextProbe = await contextEngine.Inspect(contextBackend, contextProfile);
                using var contextRunner = new RuntimeManager(contextStore);
                foreach (int requestedContext in new[] { 32768, 196608 })
                {
                    contextProfile.Options["ContextSize"] = requestedContext;
                    contextStore.Save();
                    await contextRunner.Start(contextProfile, contextModel, contextBackend, contextEngine, contextProbe);
                    Check($"Real server reports {requestedContext:N0} effective context and its actual model ID",
                        contextRunner.State == "Running" && contextRunner.LiveContext == requestedContext
                        && contextRunner.LiveModelId == contextProfile.Alias);
                    var connection = Integration.QwenConfiguration(contextStore.Value, contextProfile,
                        contextEngine.Executable(contextBackend, contextProfile), contextRunner.LiveModelId, contextRunner.LiveContext);
                    Check($"Copied Qwen connection follows the live {requestedContext:N0} context",
                        connection.I("ContextSize") == requestedContext && connection.S("ModelAlias") == contextRunner.LiveModelId);
                    using var connectionHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    connectionHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.S("ApiKey"));
                    using var connectionReply = await connectionHttp.GetAsync($"http://127.0.0.1:{connection.I("Port")}/v1/models");
                    string connectionBody = await connectionReply.Content.ReadAsStringAsync();
                    using var connectionModels = JsonDocument.Parse(connectionBody);
                    Check($"Generated Qwen credentials reach the intended {requestedContext:N0} server without 401",
                        connectionReply.IsSuccessStatusCode && connectionModels.RootElement.GetProperty("data")[0].GetProperty("id").GetString() == connection.S("ModelAlias"));
                    await contextRunner.Stop();
                }
            }
            if (args.Contains("--render"))
            {
                int renderModelIndex = Array.IndexOf(args, "--render-model");
                if (renderModelIndex >= 0 && renderModelIndex + 1 < args.Length)
                {
                    string renderModel = args[renderModelIndex + 1];
                    if (!File.Exists(renderModel))
                        throw new FileNotFoundException("Render model was not found.", renderModel);
                    int renderMtpIndex = Array.IndexOf(args, "--render-mtp");
                    string renderMtp = renderMtpIndex >= 0 && renderMtpIndex + 1 < args.Length ? args[renderMtpIndex + 1] : "";
                    var previewProfile = new Profile
                    {
                        Name = "Qwen3.8 Flash-Next Q4_K_XL — Quality-first",
                        Alias = "qwen38-flash-next-quality",
                        Path = renderModel,
                        DraftModel = renderMtp,
                        RuntimeId = "TurboQuant / Flash-Next (Experimental)",
                        Acceleration = "On",
                        Ngram = "ngram-mod",
                        TurboQuantEnabled = true,
                        TurboQuantPreset = "Quality-first",
                        MemorySafety = "Balanced"
                    };
                    previewProfile.Options["ContextSize"] = 131072;
                    previewProfile.Options["CacheK"] = "Q8_0";
                    previewProfile.Options["CacheV"] = "Q8_0";
                    previewProfile.Options["GpuLayers"] = "ALL";
                    previewProfile.Options["CpuMoe"] = true;
                    previewProfile.Options["BatchSize"] = 4096;
                    previewProfile.Options["UBatchSize"] = 2048;
                    previewProfile.Options["Threads"] = 8;
                    previewProfile.Options["ThreadsBatch"] = 8;
                    previewProfile.Options["FlashAttention"] = true;
                    previewProfile.Options["MtpDraftMax"] = 2;
                    previewProfile.Options["MtpDraftPMin"] = 0.75;
                    store.Value.Models.Clear();
                    store.Value.Models.Add(previewProfile);
                    store.Value.ActiveId = previewProfile.Id;
                    store.Value.Theme = "Dark";
                    store.Value.Backend = "CUDA";
                    store.Value.ShowTurboQuantControls = true;
                    store.Save();
                }
                if (args.Contains("--legacy"))
                    store.Import(@"C:\AI\Models\Qwen3.8-Local.json");
                // Rendering must stay on a fresh STA UI thread after asynchronous service tests.
                Exception? renderingError = null;
                var t = new Thread(() => { try { foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f }) { using var form = new MainForm(store, engine, true); form.RenderForTest(output, scale); form.Dispose(); } } catch (Exception ex) { renderingError = ex; } });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                if (renderingError != null)
                    throw renderingError;
                Check("Rendered all pages at 100/125/150/175/200 percent", true);
            }
            File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { results.Add(new { error = ex.ToString() }); File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })); return 1; }
    }
    static void WritePleShard(string path, uint shard, string architecture, bool split)
    {
        using var w = new BinaryWriter(File.Create(path));
        void Str(string value) { byte[] b = Encoding.UTF8.GetBytes(value); w.Write((ulong)b.Length); w.Write(b); }
        void Text(string key, string value) { Str(key); w.Write(8u); Str(value); }
        void Number(string key, uint value) { Str(key); w.Write(4u); w.Write(value); }
        w.Write(0x46554747u); w.Write(3u); w.Write(1ul); w.Write(split ? 6ul : 1ul);
        Text("general.architecture", architecture);
        if (split)
        {
            Number("split.no", shard); Number("split.count", 2); Number("split.tensors.count", 2);
            Number(architecture + ".ple.ngram_size", 3); Number(architecture + ".block_count", 48);
        }
        Str(shard == 1 ? "per_layer_token_embd.weight" : "token_embd.weight");
        w.Write(2u); w.Write(8ul); w.Write(16ul); w.Write(1u); w.Write(0ul);
    }

    static void WriteFixture(string path, bool mtp, string architecture = "llama")
    {
        using var w = new BinaryWriter(File.Create(path));
        void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ulong)b.Length);
            w.Write(b);
        }
        void Text(string k, string v)
        {
            Str(k);
            w.Write((uint)8);
            Str(v);
        }
        void Number(string k, uint v)
        {
            Str(k);
            w.Write((uint)4);
            w.Write(v);
        }
        w.Write(0x46554747u);
        w.Write(3u);
        w.Write(1ul);
        w.Write(mtp ? 11ul : 10ul);
        Text("general.architecture", architecture);
        Text("general.name", "Fixture");
        Number("general.file_type", 1);
        Number(architecture + ".context_length", 8192);
        Number(architecture + ".block_count", 32);
        Number(architecture + ".attention.head_count_kv", 8);
        Number(architecture + ".attention.head_count", 32);
        Number(architecture + ".embedding_length", 4096);
        Text("tokenizer.ggml.model", "llama");
        Text("tokenizer.chat_template", "{{ messages }}");
        if (mtp)
            Number(architecture + ".nextn_predict_layers", 1);
        Str(mtp ? "blk.32.nextn.eh_proj.weight" : "token_embd.weight");
        w.Write(2u);
        w.Write(4096ul);
        w.Write(32000ul);
        w.Write(1u);
        w.Write(0ul);
    }
}
