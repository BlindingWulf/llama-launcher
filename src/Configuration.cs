using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace LlamaLauncher;

public static class J
{
    public static string S(this JsonObject o, string k, string d = "") => o[k]?.ToString() ?? d;
    public static int I(this JsonObject o, string k, int d = 0) => int.TryParse(o.S(k), out var v) ? v : d;
    public static double D(this JsonObject o, string k, double d = 0) => double.TryParse(o.S(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    public static bool B(this JsonObject o, string k, bool d = false) => bool.TryParse(o.S(k), out var v) ? v : d;
    public static JsonObject Clone(this JsonObject o) => (JsonObject)o.DeepClone();
}
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Model";
    public string Path { get; set; } = "";
    public string Alias { get; set; } = "";
    public bool Legacy
    {
        get; set;
    }
    public JsonObject Options { get; set; } = Defaults();
    public JsonObject? KnownGood
    {
        get; set;
    }
    // Acceleration is retained as the persisted MTP policy name so schema-1
    // profiles migrate without losing their established behavior.
    public string Acceleration { get; set; } = "Auto";
    public string Ngram { get; set; } = "Off";
    public string DraftModel { get; set; } = "";
    public string RuntimeId { get; set; } = "Bundled";
    // PolyStrata run config (strata-<model>.json written by its setup) used when RuntimeId is PolyStrata.
    public string PolyStrataConfig { get; set; } = "";
    public string BasaltPack { get; set; } = "";
    public bool TurboQuantEnabled { get; set; }
    public string TurboQuantPreset { get; set; } = "Quality-first";
    public string TurboQuantK { get; set; } = "Q8_0";
    public string TurboQuantV { get; set; } = "Turbo4";
    public string CustomArguments { get; set; } = "";
    public string MemorySafety { get; set; } = "Balanced";
    public double CustomVramReserveGiB { get; set; } = MemoryPolicies.Balanced.VramReserveGiB;
    public double CustomRamReserveGiB { get; set; } = MemoryPolicies.Balanced.RamReserveGiB;
    public string CustomOverBudgetAction { get; set; } = "Confirm";
    public override string ToString() => Name;
    public void SetThinking(bool enabled)
    {
        if (Legacy && enabled != Options.B("Thinking"))
        {
            Options["SamplingProfile"] = enabled ? "thinking-agent" : "fast-nonthinking";
            Options["Temperature"] = enabled ? 1.0 : 0.7;
            Options["TopP"] = enabled ? 0.95 : 0.8;
            Options["TopK"] = 20;
            Options["MinP"] = 0.0;
            Options["PresencePenalty"] = enabled ? 0.0 : 1.5;
            Options["RepeatPenalty"] = 1.0;
            Options["PreserveThinking"] = enabled;
        }
        Options["Thinking"] = enabled;
    }
    public static JsonObject Defaults() => new() { ["ContextSize"] = 4096, ["NativeContextLimit"] = 262144, ["CacheK"] = "F16", ["CacheV"] = "F16", ["GpuLayers"] = "ALL", ["CpuMoe"] = false, ["CpuMoeLayers"] = 36, ["MoeCacheEnabled"] = false, ["WorkingSetLimitGiB"] = 65, ["Threads"] = Math.Max(1, Environment.ProcessorCount / 2), ["ThreadsBatch"] = Math.Max(1, Environment.ProcessorCount / 2), ["BatchSize"] = 512, ["UBatchSize"] = 128, ["FlashAttention"] = true, ["MtpEnabled"] = false, ["MtpDraftMax"] = 3, ["MtpDraftPMin"] = 0.75, ["Thinking"] = false, ["ReasoningEffort"] = "medium", ["SamplingProfile"] = "custom", ["Temperature"] = 0.7, ["TopP"] = 0.8, ["TopK"] = 20, ["MinP"] = 0.0, ["PresencePenalty"] = 1.5, ["RepeatPenalty"] = 1.0, ["PreserveThinking"] = false, ["PromptCacheMiB"] = 1024, ["Mmproj"] = "", ["ChatTemplate"] = "", ["ParallelSlots"] = 1, ["MaxOutputTokens"] = 2048 };
}
public sealed class Settings
{
    public int SchemaVersion { get; set; } = 2;
    public string Theme { get; set; } = "Dark";
    public string Backend { get; set; } = "Auto";
    public string DefaultMemorySafety { get; set; } = "Balanced";
    public string ModelsDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; } = 900;
    public int WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; }
    public bool WelcomeDone
    {
        get; set;
    }
    public bool Lan
    {
        get; set;
    }
    public int Port { get; set; } = 8080;
    public string ApiKey { get; set; } = NewKey();
    public string ActiveId { get; set; } = "";
    public bool StartupFailed
    {
        get; set;
    }
    public bool CheckUpdatesOnOpen
    {
        get; set;
    }
    public StartupRecoveryState? StartupRecovery { get; set; }
    public DetachedServerState? DetachedServer { get; set; }
    public bool ShowTurboQuantControls { get; set; }
    // Experimental alternative engine for the MoE architectures it implements; off until enabled in Advanced.
    public bool PolyStrataEnabled { get; set; }
    public string PolyStrataRoot { get; set; } = "";
    public string BasaltRoot { get; set; } = "";
    // User-added llama.cpp builds (a folder with llama-server.exe); profiles select one as "Custom: <name>".
    public List<CustomEngine> CustomEngines { get; set; } = new();
    // Engine for models added in the future: Bundled or a custom engine ID.
    public string DefaultRuntime { get; set; } = "Bundled";
    // Ollama-compatible API on its own port (Ollama.cs); off by default.
    public bool OllamaEnabled { get; set; }
    public bool OllamaTailscale { get; set; }
    public bool ModelTailscale { get; set; }
    public int ModelTailscalePort { get; set; }
    public bool OllamaRequireKey { get; set; }
    public string OllamaApiKey { get; set; } = "";
    public bool OllamaDebugLog { get; set; }   // request details for troubleshooting, never message text
    public string LegacyCommon { get; set; } = "";
    public string LegacyDesktop { get; set; } = "";
    public string LegacySettings { get; set; } = "";
    public string QwenSyncSignature { get; set; } = "";
    public List<Profile> Models { get; set; } = new();
    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
public sealed class CustomEngine
{
    public const string Prefix = "Custom: ";
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public static string IdFor(string name) => Prefix + name;
    public static bool IsCustom(string? id) => id != null && id.StartsWith(Prefix, StringComparison.Ordinal);
    public static string NameOf(string id) => IsCustom(id) ? id[Prefix.Length..] : id;
    // A custom build keeps llama-server.exe in its folder, or in cpu/cuda/vulkan subfolders like the bundled engine.
    public static bool HasServer(string? folder) => !string.IsNullOrWhiteSpace(folder)
        && (File.Exists(System.IO.Path.Combine(folder, "llama-server.exe"))
            || new[] { "cuda", "vulkan", "cpu" }.Any(b => File.Exists(System.IO.Path.Combine(folder, b, "llama-server.exe"))));
}
public sealed class StartupRecoveryState
{
    public string ProfileId { get; set; } = "";
    public string RuntimeId { get; set; } = "Bundled";
    public string Backend { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string LogFile { get; set; } = "";
    public string LaunchSummary { get; set; } = "";
    public string FirstActionableCause { get; set; } = "";
    public int? ExitCode { get; set; }
}
public sealed class DetachedServerState
{
    public int ProcessId { get; set; }
    public long StartedUtcTicks { get; set; }
    public int Port { get; set; }
    public string ProfileId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string EnginePath { get; set; } = "";
}
public sealed class ConfigStore
{
    public string Root
    {
        get;
    }
    public string FilePath => System.IO.Path.Combine(Root, "settings.json");
    public Settings Value { get; private set; } = new();
    public string Notice { get; private set; } = "";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ConfigStore(string? root = null, bool import = false, string? legacyPath = null)
    {
        Root = root ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LlamaLauncher");
        Directory.CreateDirectory(Root);
        if (File.Exists(FilePath))
        {
            try
            {
                Value = Read(FilePath);
            }
            catch
            {
                bool recovered = false;
                if (File.Exists(FilePath + ".bak"))
                    try { Value = Read(FilePath + ".bak"); recovered = true; }
                    catch { File.Copy(FilePath + ".bak", FilePath + ".bak.invalid-" + DateTime.UtcNow.Ticks); }
                if (recovered)
                    Notice = "Recovered settings from the previous valid backup.";
                else
                {
                    File.Copy(FilePath, FilePath + ".invalid-" + DateTime.UtcNow.Ticks);
                    Notice = "Settings were invalid. A backup was retained; add or import your models again.";
                }
            }
        }
        else if (import)
        {
            string legacy = legacyPath ?? @"C:\AI\Models\Qwen3.8-Local.json";
            if (File.Exists(legacy))
            {
                // Discovery is a convenience for an older local Qwen installation,
                // not a prerequisite for using the launcher. Other machines can
                // legitimately have an unrelated or older file at this path.
                var defaults = Value;
                try { Import(legacy); }
                catch
                {
                    Value = defaults;
                    Notice = "An existing Qwen configuration was left unchanged because it could not be imported. The launcher started with new settings.";
                }
            }
        }
    }
    static Settings Read(string path)
    {
        var json = File.ReadAllText(path);
        var source = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty settings");
        var v = JsonSerializer.Deserialize<Settings>(json) ?? throw new InvalidDataException("Empty settings");
        int sourceSchema = source["SchemaVersion"]?.GetValue<int>() ?? 1;
        if (sourceSchema is not (1 or 2) || v.Port < 1 || v.Port > 65535 || v.Models == null)
            throw new InvalidDataException("Unsupported settings schema or port");
        // Schema 1 profiles predate admission policies. Their estimator was advisory only,
        // so Off is the behavior-preserving migration. Newly created profiles still use
        // Settings.DefaultMemorySafety (Balanced by default).
        if (source["Models"] is JsonArray models)
            for (int i = 0; i < v.Models.Count; i++)
                if (i >= models.Count || models[i] is not JsonObject item || !item.ContainsKey("MemorySafety"))
                    v.Models[i].MemorySafety = "Off";
        if (!MemoryPolicies.Names.Contains(v.DefaultMemorySafety))
            v.DefaultMemorySafety = "Balanced";
        foreach (var profile in v.Models)
        {
            if (profile == null)
                throw new InvalidDataException("Invalid model profile");
            profile.Options ??= Profile.Defaults();
            // Deserializing an older Options object replaces the initializer;
            // merge new defaults without overwriting saved user values.
            foreach (var entry in Profile.Defaults())
                if (profile.Options[entry.Key] is null)
                    profile.Options[entry.Key] = entry.Value?.DeepClone();
            if (sourceSchema == 1)
            {
                if (profile.Acceleration == "MTP")
                    profile.Acceleration = "On";
                else if (profile.Acceleration == "N-gram (Experimental)")
                    profile.Acceleration = "Off";
                else if (profile.Acceleration != "Force MTP (Experimental)")
                    profile.Ngram = "Off";
            }
            if (!new[] { "Auto", "Off", "On", "Force MTP (Experimental)" }.Contains(profile.Acceleration))
                profile.Acceleration = "Off";
            if (!new[] { "Off", "ngram-simple", "ngram-map-k", "ngram-map-k4v", "ngram-mod", "ngram-cache" }.Contains(profile.Ngram))
                profile.Ngram = "Off";
            if (profile.RuntimeId == PolyStrata.LegacyRuntime)
                profile.RuntimeId = PolyStrata.Runtime;
            bool knownCustom = CustomEngine.IsCustom(profile.RuntimeId) && (v.CustomEngines ?? new()).Any(e => CustomEngine.IdFor(e.Name) == profile.RuntimeId);
            if (!knownCustom && !new[] { "Bundled", "TurboQuant / Flash-Next (Experimental)", PolyStrata.Runtime, Basalt.Runtime }.Contains(profile.RuntimeId))
                profile.RuntimeId = "Bundled";
            if (!new[] { "Quality-first", "Balanced", "Memory saver", "Custom" }.Contains(profile.TurboQuantPreset))
                profile.TurboQuantPreset = "Quality-first";
            if (!new[] { "Q8_0", "Turbo4", "Turbo3" }.Contains(profile.TurboQuantK))
                profile.TurboQuantK = "Q8_0";
            if (!new[] { "Q8_0", "Turbo4", "Turbo3" }.Contains(profile.TurboQuantV))
                profile.TurboQuantV = "Turbo4";
            MemoryPolicies.Normalize(profile);
        }
        v.CustomEngines = (v.CustomEngines ?? new()).Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
            .GroupBy(e => e.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (!(v.DefaultRuntime == "Bundled" || CustomEngine.IsCustom(v.DefaultRuntime) && v.CustomEngines.Any(e => CustomEngine.IdFor(e.Name) == v.DefaultRuntime)))
            v.DefaultRuntime = "Bundled";
        v.SchemaVersion = 2;
        return v;
    }
    public string RemovedModelsPath => System.IO.Path.Combine(Root, "removed-models.json");
    // Drops profiles whose GGUF was deleted, keeping them in removed-models.json so tuned settings can be
    // restored. A path on a drive that is not mounted (an unplugged disk) is left alone, as is keepId (live).
    public List<Profile> PruneMissingModels(string? keepId = null)
    {
        var missing = Value.Models.Where(p => p.Id != keepId && !string.IsNullOrWhiteSpace(p.Path)
            && !File.Exists(p.Path) && System.IO.Path.GetPathRoot(p.Path) is string drive
            && drive.Length > 0 && Directory.Exists(drive)).ToList();
        if (missing.Count == 0)
            return missing;
        var archived = new List<Profile>();
        if (File.Exists(RemovedModelsPath))
            try { archived = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(RemovedModelsPath), Json) ?? new(); }
            catch { File.Move(RemovedModelsPath, RemovedModelsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss")); }
        archived.RemoveAll(a => missing.Any(m => m.Id == a.Id));
        archived.AddRange(missing);
        File.WriteAllText(RemovedModelsPath, JsonSerializer.Serialize(archived, Json));
        foreach (var p in missing)
            Value.Models.Remove(p);
        if (!Value.Models.Any(p => p.Id == Value.ActiveId))
            Value.ActiveId = Value.Models.FirstOrDefault()?.Id ?? "";
        Save();
        return missing;
    }
    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Value, Json));
        Read(tmp);
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, FilePath + ".bak");
        else
            File.Move(tmp, FilePath);
    }
    public void Import(string path)
    {
        var o = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Invalid legacy JSON");
        if (o.I("SchemaVersion") != 3 || o["Models"] is not JsonArray models)
            throw new InvalidDataException("Expected Qwen launcher schema 3");
        var next = new Settings { Lan = o.B("LanAccess"), Port = o.I("Port", 8080), ApiKey = o.S("ApiKey"), ActiveId = o.S("ActiveModelId"), WelcomeDone = true, ModelsDirectory = System.IO.Path.GetDirectoryName(path)!, LegacyCommon = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "Qwen3.8-Common.ps1"), LegacyDesktop = o.S("QwenDesktop"), LegacySettings = o.S("QwenSettings") };
        foreach (var entry in models.OfType<JsonObject>())
        {
            var opts = o.Clone();
            opts.Remove("Models");
            opts.Remove("ApiKey");
            opts.Remove("QwenSettings");
            opts.Remove("QwenDesktop");
            opts.Remove("LlamaServer");
            foreach (var kv in entry)
                if (kv.Key is not "SavedSettings" and not "OptimizedDefaults")
                    opts[kv.Key] = kv.Value?.DeepClone();
            var saved = entry["SavedSettings"] as JsonObject ?? entry["OptimizedDefaults"] as JsonObject;
            if (saved != null)
                foreach (var kv in saved)
                    opts[kv.Key] = kv.Value?.DeepClone();
            if (entry.S("Id") == next.ActiveId)
                foreach (var key in Profile.Defaults().Select(k => k.Key).Concat(new[] { "Temperature", "TopP", "TopK", "MinP", "PresencePenalty", "RepeatPenalty", "PreserveThinking", "SamplingProfile" }))
                    if (o.ContainsKey(key))
                        opts[key] = o[key]?.DeepClone();
            next.Models.Add(new Profile { Id = entry.S("Id"), Name = entry.S("DisplayName"), Path = entry.S("Model"), Alias = entry.S("ModelAlias"), Legacy = true, Options = opts, KnownGood = (entry["OptimizedDefaults"] as JsonObject)?.Clone(), Acceleration = opts.B("MtpEnabled") ? "On" : "Off", MemorySafety = "Off" });
        }
        File.Copy(path, System.IO.Path.Combine(Root, "legacy-import-" + DateTime.UtcNow.Ticks + ".json"));
        var active = next.Models.FirstOrDefault(p => p.Id == next.ActiveId);
        if (active != null)
            next.QwenSyncSignature = Integration.Fingerprint(next, active);
        Value = next;
        Save();
        Notice = "Imported existing Qwen profiles. Original settings and Qwen Code data were preserved.";
    }
}
