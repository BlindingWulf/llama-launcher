using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LlamaLauncher;

// Basalt is a separate server and uses a converted model pack, while the profile's GGUF remains the identity.
public static class Basalt
{
    public const string Runtime = "Basalt";
    public const string Repository = "jesdga95/basalt";
    public sealed record Installed(string Version, bool Custom, string Build);
    public sealed record Release(string Tag, string Url);

    // The installed Basalt version. A hand-built or hotfixed binary records its base release in bin\*HOTFIX*.json;
    // a stock release embeds "engine=X.Y.Z" in basalt.exe. Returns null when neither is found.
    public static Installed? InstalledVersion(string root)
    {
        string bin = Path.Combine(root, "bin");
        if (!Directory.Exists(bin))
            return null;
        foreach (var file in Directory.EnumerateFiles(bin, "*HOTFIX*.json"))
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("base_release", out var baseRelease) && baseRelease.GetString() is string tag && tag.Length > 0)
                    return new Installed(tag.TrimStart('v'), true, doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        string exe = Engine(root);
        if (!File.Exists(exe))
            return null;
        try
        {
            var bytes = File.ReadAllBytes(exe);
            var marker = Encoding.ASCII.GetBytes("engine=");
            int at = bytes.AsSpan().IndexOf(marker);
            if (at >= 0)
            {
                int start = at + marker.Length, end = start;
                while (end < bytes.Length && end - start < 24 && (bytes[end] == (byte)'.' || (bytes[end] >= (byte)'0' && bytes[end] <= (byte)'9')))
                    end++;
                string version = Encoding.ASCII.GetString(bytes, start, end - start).Trim('.');
                if (System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+(\.\d+){1,3}$"))
                    return new Installed(version, false, "");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public static async Task<Release> LatestRelease()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaLauncher");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string url = doc.RootElement.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v?[\w.\-]+$") || !url.StartsWith("https://github.com/" + Repository + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub returned an unexpected Basalt release.");
        return new Release(tag, url);
    }

    public static bool IsNewer(string tag, string installed) =>
        Version.TryParse(tag.TrimStart('v').Split('-', '+')[0], out var latest) && Version.TryParse(installed.Split('-', '+')[0], out var current) && latest > current;
    public static string Python(string root) => Path.Combine(root, ".venv", "Scripts", "python.exe");
    public static string Engine(string root) => Path.Combine(root, "bin", "basalt.exe");
    public static bool ModelLoaded(JsonElement entry) => entry.TryGetProperty("status", out var status)
        && status.ValueKind == JsonValueKind.Object && status.TryGetProperty("value", out var state)
        && state.ValueKind == JsonValueKind.String && state.GetString() == "loaded";
    public static string? Problem(string root, string pack, ModelInfo? model)
    {
        if (model == null || !model.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase))
            return "Basalt is available for Qwen3.8 Flash-Next profiles.";
        if (!File.Exists(Python(root)) || !File.Exists(Engine(root)) || !File.Exists(Path.Combine(root, "serve", "server.py")))
            return "Choose an installed Basalt folder with its Python environment under Advanced.";
        if (!File.Exists(pack)) return "Choose a converted .basalt pack for this model under Advanced.";
        try
        {
            using var stream = File.OpenRead(pack);
            using var reader = new BinaryReader(stream, Encoding.ASCII);
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "BASALTMF" || reader.ReadUInt32() != 2)
                return "The selected file is not a Basalt model pack.";
            uint count = reader.ReadUInt32();
            ulong total = reader.ReadUInt64();
            if (count > 511 || total != (ulong)stream.Length) return "The Basalt pack is incomplete or has an invalid header.";
            stream.Position = 64;
            var sections = new HashSet<string>(StringComparer.Ordinal);
            for (uint i = 0; i < count; i++)
            {
                string name = Encoding.ASCII.GetString(reader.ReadBytes(64)).TrimEnd('\0');
                ulong offset = reader.ReadUInt64(), size = reader.ReadUInt64();
                if (offset > total || size > total - offset) return "The Basalt pack has an invalid section.";
                sections.Add(name);
                stream.Position += 48;
            }
            if (!sections.Contains("vision/mmproj")) return "The Basalt pack has no image projector. Convert it with --mmproj.";
            if (!new[] { "mtp/dense", "mtp/dense.index", "mtp/experts" }.All(sections.Contains))
                return "The Basalt pack has no complete MTP drafter. Convert it with --mtp-source.";
        }
        catch (IOException ex) { return "Cannot read Basalt pack: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { return "Cannot read Basalt pack: " + ex.Message; }
        return null;
    }
    public static List<string> Arguments(Settings s, Profile p)
    {
        string k = p.Options.S("CacheK", "Q8_0"), v = p.Options.S("CacheV", "Q8_0");
        string kv = k.Equals("F16", StringComparison.OrdinalIgnoreCase) || v.Equals("F16", StringComparison.OrdinalIgnoreCase)
            ? "fp16" : PolyStrata.KvFor(k, v).Kv;
        return new() { "-m", "serve.server", "--engine-bin", Engine(s.BasaltRoot), "--model", p.BasaltPack,
            "--alias", p.Alias, "--host", "127.0.0.1", "--port", s.Port.ToString(CultureInfo.InvariantCulture),
            "--api-key", s.ApiKey, "--gpu", "0", "--vision", "dynamic", "--max-context",
            p.Options.I("ContextSize").ToString(CultureInfo.InvariantCulture), "--kv", kv, "--spec", "8",
            "--parallel", Math.Clamp(p.Options.I("PolyParallelSlots", 1), 1, 8).ToString(CultureInfo.InvariantCulture),
            // Park long conversations (8,192+ tokens) on disk when another request takes the slot, and read them
            // back instead of re-reading the whole prompt. Apps such as Qwen Code send side requests (memory,
            // titles) between turns; without this each one forced a full re-read of a 50-75k token context
            // (7-40 s to first token). The disk tier, in Basalt's model cache folder, leaves the RAM the expert
            // weights need untouched, keeps 4 GiB of the drive free, and also survives a restart.
            "--conversation-cache-disk-mib", ConversationCacheDiskMib.ToString(CultureInfo.InvariantCulture) };
    }
    public const int ConversationCacheDiskMib = 24576;
}
