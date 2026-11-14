using System.Text;

namespace LlamaLauncher;

public sealed class ModelInfo
{
    public string Name { get; set; } = "Unknown";
    public string Architecture { get; set; } = "Unknown";
    public string Quantization { get; set; } = "Unknown";
    public long Bytes
    {
        get; set;
    }
    public ulong Parameters
    {
        get; set;
    }
    public long NativeContext
    {
        get; set;
    }
    public int Layers
    {
        get; set;
    }
    public int KvHeads
    {
        get; set;
    }
    public int KeyLength
    {
        get; set;
    }
    public int ValueLength
    {
        get; set;
    }
    public int FullAttentionInterval
    {
        get; set;
    }
    public string Mtp { get; set; } = "Unknown";
    public string MtpEvidence { get; set; } = "No dependable MTP metadata or tensor evidence.";
    public bool HasTemplate
    {
        get; set;
    }
    public bool Reasoning
    {
        get; set;
    }
    public string Tokenizer { get; set; } = "Unknown";
    public string Vision { get; set; } = "Unknown; add a matching projector under Advanced if required.";
    public bool HasPle { get; set; }
    public ulong PleParameters { get; set; }
    // The measured placement recipe belongs to this exact Q4 Flash-Next layout,
    // not to every future model sharing the architecture name.
    public bool UsesValidatedFlashNextPreset => Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)
        && Layers == 48 && Bytes == 111_334_654_784L;
    public double CoreParameterBillions => (Parameters - Math.Min(Parameters, PleParameters)) / 1e9;
    public bool HasHybridState => FullAttentionInterval > 0 || Metadata.Keys.Any(k =>
        k.StartsWith(Architecture + ".ssm.", StringComparison.OrdinalIgnoreCase)
        || k.Equals(Architecture + ".attention.recurrent_layers", StringComparison.OrdinalIgnoreCase));
    public uint Version
    {
        get; set;
    }
    public int Shards { get; set; } = 1;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public double GiB => Bytes / 1073741824d;
    public string Summary => $"{Architecture}  ·  {(Parameters > 0 ? $"{Parameters / 1e9:0.##}B" : "Parameters unknown")}  ·  {Quantization}  ·  {GiB:0.0} GB";
}
public static class GgufInspector
{
    const ulong MaxElements = 20_000_000;
    static string Str(BinaryReader r, bool keep = true)
    {
        ulong n = r.ReadUInt64();
        if (n > 256 * 1024 * 1024 || n > (ulong)(r.BaseStream.Length - r.BaseStream.Position))
            throw new InvalidDataException("Invalid GGUF string length");
        if (!keep || n > 1024 * 1024)
        {
            r.BaseStream.Seek((long)n, SeekOrigin.Current);
            return "";
        }
        return Encoding.UTF8.GetString(r.ReadBytes((int)n));
    }
    static object Value(BinaryReader r, uint type, bool keep, int depth = 0)
    {
        if (depth > 4)
            throw new InvalidDataException("Nested GGUF array exceeds inspection limit");
        switch (type)
        {
            case 0:
                return r.ReadByte();
            case 1:
                return r.ReadSByte();
            case 2:
                return r.ReadUInt16();
            case 3:
                return r.ReadInt16();
            case 4:
                return r.ReadUInt32();
            case 5:
                return r.ReadInt32();
            case 6:
                return r.ReadSingle();
            case 7:
                return r.ReadBoolean();
            case 8:
                return Str(r, keep);
            case 10:
                return r.ReadUInt64();
            case 11:
                return r.ReadInt64();
            case 12:
                return r.ReadDouble();
            case 9:
                uint t = r.ReadUInt32();
                ulong count = r.ReadUInt64();
                if (count > MaxElements)
                    throw new InvalidDataException("GGUF array exceeds inspection limit");
                var size = t switch
                {
                    0 or 1 or 7 => 1,
                    2 or 3 => 2,
                    4 or 5 or 6 => 4,
                    10 or 11 or 12 => 8,
                    _ => 0
                };
                if (size > 0)
                {
                    long bytes = checked((long)count * size);
                    if (bytes > r.BaseStream.Length - r.BaseStream.Position)
                        throw new EndOfStreamException();
                    r.BaseStream.Seek(bytes, SeekOrigin.Current);
                }
                else
                    for (ulong i = 0; i < count; i++)
                        Value(r, t, false, depth + 1);
                return $"Array ({count} entries)";
            default:
                throw new InvalidDataException("Unknown GGUF value type " + type);
        }
    }
    public static ModelInfo Inspect(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new BinaryReader(stream);
        if (r.ReadUInt32() != 0x46554747)
            throw new InvalidDataException("Not a GGUF model");
        var info = new ModelInfo { Bytes = stream.Length, Version = r.ReadUInt32() };
        if (info.Version is not 2 and not 3)
            throw new InvalidDataException("Only GGUF v2/v3 are supported");
        ulong tensors = r.ReadUInt64(), kv = r.ReadUInt64();
        if (tensors > 2_000_000 || kv > 100_000)
            throw new InvalidDataException("Unreasonable GGUF header count");
        for (ulong i = 0; i < kv; i++)
        {
            string key = Str(r);
            uint type = r.ReadUInt32();
            info.Metadata[key] = Value(r, type, true);
        }
        long Num(string key, long fallback = 0)
        {
            try
            {
                return Convert.ToInt64(info.Metadata.GetValueOrDefault(key, fallback));
            }
            catch { return fallback; }
        }
        string Text(string key, string fallback = "Unknown") => info.Metadata.GetValueOrDefault(key, fallback)?.ToString() ?? fallback;
        info.Architecture = Text("general.architecture");
        info.Name = Text("general.name", System.IO.Path.GetFileNameWithoutExtension(path));
        string a = info.Architecture;
        info.NativeContext = Num(a + ".context_length");
        info.Layers = (int)Num(a + ".block_count");
        info.KvHeads = (int)Num(a + ".attention.head_count_kv");
        var heads = Num(a + ".attention.head_count", 1);
        var embed = Num(a + ".embedding_length");
        info.KeyLength = (int)Num(a + ".attention.key_length", heads > 0 ? embed / heads : 0);
        info.ValueLength = (int)Num(a + ".attention.value_length", info.KeyLength);
        info.FullAttentionInterval = (int)Num(a + ".full_attention_interval");
        info.Tokenizer = Text("tokenizer.ggml.model");
        var template = Text("tokenizer.chat_template", "");
        info.HasTemplate = template.Length > 0;
        info.Reasoning = template.Contains("enable_thinking") || template.Contains("reasoning_effort") || template.Contains("<think>");
        if (info.Metadata.Keys.Any(k => k.StartsWith("clip.")))
            info.Vision = "Vision projector metadata detected";
        int fileType = (int)Num("general.file_type", -1);
        info.Quantization = fileType switch
        {
            0 => "F32",
            1 => "F16",
            2 => "Q4_0",
            3 => "Q4_1",
            7 => "Q8_0",
            8 => "Q5_0",
            9 => "Q5_1",
            10 => "Q2_K",
            11 => "Q3_K_S",
            12 => "Q3_K_M",
            13 => "Q3_K_L",
            14 => "Q4_K_S",
            15 => "Q4_K_M",
            16 => "Q5_K_S",
            17 => "Q5_K_M",
            18 => "Q6_K",
            30 => "BF16",
            _ => $"GGUF type {fileType} (mixed/other)"
        };
        bool mtpTensor = false;
        ulong perLayerEmbeddingParameters = 0;
        ulong totalTensorDescriptors = 0;
        var types = new Dictionary<uint, int>();
        void ScanTensors(BinaryReader reader, ulong count)
        {
            for (ulong i = 0; i < count; i++)
            {
                string name = Str(reader);
                uint dims = reader.ReadUInt32();
                if (dims > 8)
                    throw new InvalidDataException("Invalid tensor dimensions");
                ulong p = 1;
                for (int j = 0; j < dims; j++)
                    p = checked(p * reader.ReadUInt64());
                info.Parameters = checked(info.Parameters + p);
                if (name == "per_layer_token_embd.weight")
                {
                    perLayerEmbeddingParameters = checked(perLayerEmbeddingParameters + p);
                }
                uint t = reader.ReadUInt32();
                types[t] = types.GetValueOrDefault(t) + 1;
                ulong offset = reader.ReadUInt64();
                if (offset > (ulong)reader.BaseStream.Length)
                    throw new InvalidDataException("Invalid tensor offset");
                if (name.Contains(".nextn.") || name.StartsWith("mtp.") || name.Contains(".mtp.") || name.Contains("nextn_predict"))
                    mtpTensor = true;
            }
            totalTensorDescriptors += count;
        }
        ScanTensors(r, tensors);
        info.Shards = (int)Num("split.count", 1);
        info.HasPle |= info.Metadata.Keys.Any(k => k.StartsWith(a + ".ple.", StringComparison.OrdinalIgnoreCase));
        if (info.Shards > 1)
        {
            if (Num("split.no") != 0)
                throw new InvalidDataException("Select shard 00001; keep all shards in the same folder.");
            var file = System.IO.Path.GetFileName(path);
            var match = System.Text.RegularExpressions.Regex.Match(file, @"^(.*)-00001-of-(\d{5})\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new InvalidDataException("Split GGUF needs standard -00001-of-NNNNN.gguf filenames.");
            for (int i = 2; i <= info.Shards; i++)
            {
                var shard = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{match.Groups[1].Value}-{i:00000}-of-{info.Shards:00000}.gguf");
                if (!File.Exists(shard))
                    throw new FileNotFoundException("A GGUF shard is missing");
                using var shardStream = File.Open(shard, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var shardReader = new BinaryReader(shardStream);
                if (shardReader.ReadUInt32() != 0x46554747 || shardReader.ReadUInt32() != info.Version)
                    throw new InvalidDataException($"Shard {i} is not a matching GGUF version.");
                ulong shardTensors = shardReader.ReadUInt64(), shardKv = shardReader.ReadUInt64();
                if (shardTensors > 2_000_000 || shardKv > 100_000)
                    throw new InvalidDataException($"Shard {i} has unreasonable header counts.");
                long splitNo = -1, splitCount = -1, splitTensorCount = -1;
                for (ulong k = 0; k < shardKv; k++)
                {
                    string key = Str(shardReader);
                    uint type = shardReader.ReadUInt32();
                    object item = Value(shardReader, type, key is "split.no" or "split.count" or "split.tensors.count");
                    if (key.StartsWith(a + ".ple.", StringComparison.OrdinalIgnoreCase))
                        info.HasPle = true;
                    try
                    {
                        if (key == "split.no") splitNo = Convert.ToInt64(item);
                        else if (key == "split.count") splitCount = Convert.ToInt64(item);
                        else if (key == "split.tensors.count") splitTensorCount = Convert.ToInt64(item);
                    }
                    catch { }
                }
                if (splitNo != i - 1 || splitCount != info.Shards || (splitTensorCount >= 0 && splitTensorCount != Num("split.tensors.count")))
                    throw new InvalidDataException($"Shard {i} belongs to a different or malformed split group.");
                ScanTensors(shardReader, shardTensors);
                info.Bytes += shardStream.Length;
            }
            long declaredTensorCount = Num("split.tensors.count", -1);
            if (declaredTensorCount >= 0 && totalTensorDescriptors != (ulong)declaredTensorCount)
                throw new InvalidDataException($"Split GGUF declares {declaredTensorCount} tensors but contains {totalTensorDescriptors} descriptors.");
        }
        // Per-layer embeddings also occur in other families; only PLE metadata
        // or the established qwen4exp layout identifies them as learned n-grams.
        info.HasPle |= a.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase) && perLayerEmbeddingParameters > 0;
        if (info.HasPle)
            info.PleParameters = perLayerEmbeddingParameters;
        if (Num(a + ".nextn_predict_layers") > 0 && mtpTensor)
        {
            info.Mtp = "Supported";
            info.MtpEvidence = "GGUF declares next-token prediction layers and contains matching MTP tensors.";
        }
        else if (mtpTensor)
        {
            info.Mtp = "Supported";
            info.MtpEvidence = "MTP/nextn tensors found in the complete GGUF shard group; runtime architecture support is still required.";
        }
        else if (info.Metadata.ContainsKey(a + ".nextn_predict_layers") && Num(a + ".nextn_predict_layers") == 0)
        {
            info.Mtp = "Unavailable";
            info.MtpEvidence = "GGUF explicitly declares zero next-token prediction layers.";
        }
        return info;
    }
}
