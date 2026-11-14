using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlamaLauncher;
public static class Arguments
{
    public sealed record CacheSelection(string K, string V, bool TurboQuant, string Preset);

    public static CacheSelection EffectiveCache(Profile p, ModelInfo? model = null)
    {
        if (!p.TurboQuantEnabled)
            return new(p.Options.S("CacheK", "F16"), p.Options.S("CacheV", "F16"), false, "Off");
        return p.TurboQuantPreset switch
        {
            "Balanced" => new("Turbo4", "Turbo4", true, p.TurboQuantPreset),
            // Flash-Next's indexer cannot read TurboQuant keys. Keep Turbo3 on V
            // only: Turbo3 keys are especially risky on high-GQA models.
            "Memory saver" when model?.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase) == true
                => new("Q8_0", "Turbo3", true, p.TurboQuantPreset),
            "Memory saver" => new("Turbo4", "Turbo3", true, p.TurboQuantPreset),
            "Custom" => new(p.TurboQuantK, p.TurboQuantV, true, p.TurboQuantPreset),
            _ => new("Q8_0", "Turbo4", true, "Quality-first")
        };
    }

    public static bool MtpActive(Profile p, ModelInfo m) => p.Acceleration switch
    {
        "On" or "Force MTP (Experimental)" => true,
        "Auto" => m.Mtp == "Supported" || File.Exists(p.DraftModel),
        _ => false
    };

    public static List<string> Build(Profile p, Settings s, ModelInfo m, string backend, string help)
    {
        var o = p.Options;
        var capabilities = new EngineCapabilities(help);
        var a = new List<string>();
        void Add(string flag, object? value = null)
        {
            a.Add(flag);
            if (value != null)
                a.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        }
        int ctx = o.I("ContextSize");
        if (ctx < 256 || ctx > 1048576)
            throw new ArgumentException("Context must be 256–1,048,576 tokens.");
        if (o.I("UBatchSize") > o.I("BatchSize"))
            throw new ArgumentException("UBatch must not exceed batch size.");
        if (s.Lan && string.IsNullOrWhiteSpace(s.ApiKey))
            throw new ArgumentException("LAN requires an API key.");
        if (o.S("GpuLayers").ToUpperInvariant() != "ALL" && (!int.TryParse(o.S("GpuLayers"), out int layers) || layers < 0))
            throw new ArgumentException("GPU layers must be ALL or a nonnegative integer.");
        Add("-m", p.Path);
        Add("--alias", p.Alias);
        Add("--host", s.Lan ? "0.0.0.0" : "127.0.0.1");
        Add("--port", s.Port);
        Add("--api-key", s.ApiKey);
        if (backend == "CUDA")
            Add("--device", "CUDA0");
        else if (backend == "CPU")
            Add("--device", "none");
        Add("--split-mode", "none");
        Add("--gpu-layers", backend == "CPU" ? "0" : o.S("GpuLayers").ToLowerInvariant());
        if (backend != "CPU" && o.B("CpuMoe"))
        {
            if (!capabilities.HasFlag("--cpu-moe"))
                throw new ArgumentException("The selected runtime does not support explicit CPU MoE placement.");
            bool flashNext = m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase);
            int cpuMoeLayers = Math.Clamp(o.I("CpuMoeLayers", 36), 0, Math.Max(0, m.Layers));
            if (flashNext && cpuMoeLayers > 0 && cpuMoeLayers < m.Layers && capabilities.HasFlag("--n-cpu-moe"))
                Add("--n-cpu-moe", cpuMoeLayers);
            else
                Add("--cpu-moe");
            if (capabilities.HasFlag("--moe-cache"))
                Add("--moe-cache", flashNext && !o.B("MoeCacheEnabled") ? "off" : "auto");
        }
        Add("--fit", "off");
        // Flash-Next carries a ~28.8 GB learned PLE N-gram embedding table.
        // The patched experimental runtime marks that tensor as lazy-readable;
        // request the mode explicitly so only gathered rows are faulted from the
        // mmap-backed GGUF instead of prefetching the full table into host RAM.
        if ((m.HasPle || m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase))
            && capabilities.HasFlag("--tensor-read-lazy"))
            Add("--tensor-read-lazy", "on");
        // Keep one complete Qwen Code agent prefix resident. Its typical 28.5K
        // token cold prompt occupies a little over 1 GiB, so a 1 GiB limit
        // evicts the prefix and makes every apparent "warm" turn cold again.
        // Two GiB retains that prefix while the process working-set ceiling
        // still prevents the mmap-backed expert store from exhausting RAM.
        if (m.UsesValidatedFlashNextPreset && capabilities.HasFlag("--cache-ram"))
            Add("--cache-ram", Math.Clamp(Math.Max(2048, o.I("PromptCacheMiB", 2048)), 2048, 4096));
        Add("--flash-attn", o.B("FlashAttention") ? "on" : "off");
        var cache = EffectiveCache(p, m);
        if (cache.TurboQuant)
        {
            if (!s.ShowTurboQuantControls)
                throw new ArgumentException("TurboQuant is active for this model but its experimental controls are hidden. Show the controls or turn TurboQuant off explicitly.");
            if (backend != "CUDA")
                throw new ArgumentException("TurboQuant is currently validated only on the CUDA experimental runtime.");
            if (!o.B("FlashAttention"))
                throw new ArgumentException("TurboQuant requires Flash Attention. Turn Flash Attention on or disable TurboQuant.");
            if (m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)
                && cache.K.StartsWith("Turbo", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("TurboQuant key compression is unavailable for Flash-Next: its separate indexer K cache reaches an unsupported CPU GET_ROWS path. Use Quality-first (Q8 K / Turbo4 V).");
            if (!help.Contains("turbo4", StringComparison.OrdinalIgnoreCase)
                || (new[] { cache.K, cache.V }.Any(x => x.Equals("Turbo3", StringComparison.OrdinalIgnoreCase)) && !help.Contains("turbo3", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The selected runtime does not provide the requested native TurboQuant cache codec.");
        }
        Add("--cache-type-k", cache.K.ToLowerInvariant());
        Add("--cache-type-v", cache.V.ToLowerInvariant());
        Add("--ctx-size", ctx);
        Add("--batch-size", o.I("BatchSize"));
        Add("--ubatch-size", o.I("UBatchSize"));
        Add("--threads", o.I("Threads"));
        Add("--threads-batch", o.I("ThreadsBatch", o.I("Threads")));
        Add("--parallel", o.I("ParallelSlots", 1));
        if (o.B("ContextCacheInRam"))
        {
            if (!capabilities.HasFlag("--no-kv-offload"))
                throw new ArgumentException("The selected llama.cpp runtime does not support keeping the context cache in RAM.");
            Add("--no-kv-offload");
        }
        if (p.Legacy || m.Reasoning)
            Add("--reasoning", o.B("Thinking") ? "on" : "off");
        if (!p.Legacy)
            Add("--temp", o.S("Temperature", "0.7"));
        if (p.Legacy)
        {
            foreach (var pair in new[] { ("--temp", "Temperature"), ("--top-p", "TopP"), ("--top-k", "TopK"), ("--min-p", "MinP"), ("--presence-penalty", "PresencePenalty"), ("--repeat-penalty", "RepeatPenalty") })
                Add(pair.Item1, o.S(pair.Item2));
            Add("--cache-prompt");
            Add("--cache-ram", o.I("PromptCacheMiB"));
            Add("--image-min-tokens", o.I("ImageMinTokens"));
            Add("--check-tensors");
        }
        Add("--no-webui");
        if (!string.IsNullOrWhiteSpace(o.S("Mmproj")))
        {
            if (!File.Exists(o.S("Mmproj")))
                throw new FileNotFoundException("Vision projector is missing");
            Add("--mmproj", o.S("Mmproj"));
        }
        Add("--jinja");
        if ((p.Legacy || m.Reasoning) && o.B("Thinking"))
        {
            // Newer llama.cpp accepts reasoning effort per API request rather than
            // as a server startup option. Preserve the profile value and emit the
            // startup flag only for runtimes that still advertise it.
            if (capabilities.HasFlag("--reasoning-effort"))
                Add("--reasoning-effort", o.S("ReasoningEffort", "medium").ToLowerInvariant());
            if (o.B("PreserveThinking"))
                Add("--reasoning-preserve");
        }
        // Upstream llama.cpp now preserves reasoning in history by default (b11xxx+). Clients can
        // still request thinking per call on a non-thinking profile, so state "off" explicitly.
        if ((p.Legacy || m.Reasoning) && !(o.B("Thinking") && o.B("PreserveThinking"))
            && capabilities.HasFlag("--no-reasoning-preserve"))
            Add("--no-reasoning-preserve");
        if (!string.IsNullOrWhiteSpace(o.S("ChatTemplate")))
        {
            if (!File.Exists(o.S("ChatTemplate")))
                throw new FileNotFoundException("Chat template is missing");
            Add("--chat-template-file", o.S("ChatTemplate"));
        }
        var specTypes = new List<string>();
        bool mtp = MtpActive(p, m);
        if (mtp)
        {
            bool hasExternalDraft = !string.IsNullOrWhiteSpace(p.DraftModel);
            if (hasExternalDraft && !File.Exists(p.DraftModel))
                throw new FileNotFoundException("The selected MTP draft-head file is missing.", p.DraftModel);
            if (m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase) && m.Mtp != "Supported" && !hasExternalDraft)
                throw new ArgumentException("Flash-Next MTP requires the separate MTP-head GGUF. Select the verified head under Advanced; Force MTP cannot manufacture draft layers inside the base model.");
            if (m.Mtp != "Supported" && !hasExternalDraft && p.Acceleration != "Force MTP (Experimental)" && !(p.Legacy && p.KnownGood?.B("MtpEnabled") == true))
                throw new ArgumentException("MTP requires a compatible draft-head file for this model. Select one, or use Force MTP only for a deliberately unsupported experiment.");
            // A DFlash 2 drafter (general.architecture "dflash") drafts a whole block per pass from the target's hidden
            // states; llama.cpp runs it as its own speculative type, not as an MTP head.
            string draftType = hasExternalDraft && GgufInspector.Inspect(p.DraftModel).Architecture.Equals("dflash", StringComparison.OrdinalIgnoreCase)
                ? "draft-dflash" : "draft-mtp";
            if (!capabilities.HasSpecType(draftType))
                throw new ArgumentException($"This engine does not support {draftType}. Retain or roll back to the bundled engine.");
            specTypes.Add(draftType);
            if (hasExternalDraft)
                Add(capabilities.RequiredFlag("--spec-draft-model", "--model-draft"), p.DraftModel);
            if (backend == "CUDA" && capabilities.HasFlag("--spec-draft-ngl"))
                Add("--spec-draft-ngl", 999);
            // Zero/omitted means "use the draft model's trained context". Flash-Next
            // advertises 262K, which otherwise makes a 64K launch allocate a 262K MTP
            // cache. Keep the target and draft contexts aligned with the user's choice.
            if (capabilities.HasFlag("--ctx-size-draft"))
                Add("--ctx-size-draft", Math.Max(512, o.I("ContextSize", 4096)));
            if (capabilities.HasFlag("--spec-draft-type-k"))
            {
                Add("--spec-draft-type-k", "q8_0");
                Add("--spec-draft-type-v", "q8_0");
            }
            if (capabilities.HasFlag("--spec-draft-p-min"))
                Add("--spec-draft-p-min", Math.Clamp(o.D("MtpDraftPMin", 0.75), 0, 1));
            // Rejection-sampled verification keeps the target model's output distribution and accepts more
            // drafted tokens than an exact match. Engines without the flag fail the capability check below.
            if (o.S("MtpDraftSampling", "greedy") == "probabilistic")
                Add("--spec-draft-sampling", "probabilistic");
        }
        if (p.Ngram != "Off")
        {
            if (!new[] { "ngram-simple", "ngram-map-k", "ngram-map-k4v", "ngram-mod", "ngram-cache" }.Contains(p.Ngram) || !capabilities.HasSpecType(p.Ngram))
                throw new ArgumentException("Selected n-gram mode is unavailable in this engine.");
            specTypes.Add(p.Ngram);
        }
        if (specTypes.Count > 0)
        {
            Add("--spec-type", string.Join(',', specTypes));
            if (mtp)
                Add(capabilities.RequiredFlag("--spec-draft-n-max", "--draft-max"), o.I("MtpDraftMax", 2));
        }
        // Deliberately narrow extension point: options that alter ownership, networking, models,
        // resource allocation or speculation must go through managed controls.
        var custom = Regex.Matches(p.CustomArguments, @"""[^""]*""|\S+").Select(x => x.Value.Trim('"')).ToArray();
        var allowed = new HashSet<string> { "--seed", "--temp", "--top-p", "--top-k", "--min-p", "--repeat-penalty", "--presence-penalty", "--frequency-penalty" };
        for (int i = 0; i < custom.Length; i += 2)
        {
            if (i + 1 >= custom.Length || !allowed.Contains(custom[i]) || a.Contains(custom[i]) || !double.TryParse(custom[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("Custom arguments allow numeric sampling options only; managed flags and duplicates are rejected.");
            Add(custom[i], custom[i + 1]);
        }
        foreach (var flag in a.Where(x => x.StartsWith("--")))
            if (!capabilities.HasFlag(flag))
                throw new ArgumentException($"Engine does not recognize {flag}; select a compatible engine or restore safe settings.");
        return a;
    }
    public static string Sanitize(IEnumerable<string> args, Settings s, Profile p)
    {
        var list = args.ToArray();
        for (int i = 0; i < list.Length; i++)
        {
            if (i > 0 && list[i - 1] is "--api-key" or "-m" or "--model" or "--mmproj" or "--chat-template-file" or "--spec-draft-model" or "--model-draft")
                list[i] = "<redacted>";
        }
        return string.Join(" ", list);
    }
}
