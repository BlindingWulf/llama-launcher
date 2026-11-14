using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LlamaLauncher;
public sealed record Probe(string Version, string Help, string Devices);
public sealed record EngineReleaseAsset(string Name, string Url, long Size, string Sha256);
public sealed record EngineRelease(string Tag, string HtmlUrl, DateTimeOffset PublishedAt,
    EngineReleaseAsset Cpu, EngineReleaseAsset Cuda, EngineReleaseAsset CudaRuntime,
    EngineReleaseAsset Vulkan)
{
    public IReadOnlyList<EngineReleaseAsset> RequiredAssets => new[] { Cpu, Cuda, CudaRuntime, Vulkan };
    public long DownloadBytes => RequiredAssets.Sum(a => a.Size);
}
public sealed record EngineUpdateCheck(EngineRelease Release, string InstalledVersion)
{
    public bool UpdateAvailable => EngineManager.TryBuildNumber(Release.Tag, out int latest)
        && EngineManager.TryBuildNumber(InstalledVersion, out int installed) && latest > installed;
}
public sealed record EngineUpdateProgress(string Stage, string AssetName = "",
    long AssetDownloaded = 0, long AssetSize = 0, long TotalDownloaded = 0, long TotalSize = 0);
public sealed class EngineState
{
    public string Current { get; set; } = ""; public string Previous { get; set; } = "";
}
public sealed class EngineManager
{
    public const string BundledRuntime = "Bundled";
    public const string FlashNextRuntime = "TurboQuant / Flash-Next (Experimental)";
    public const string FlashNextMtpFile = "Qwen3.8-Flash-Next-MTP-Q4_K_M.gguf";
    readonly string appRoot, dataRoot; EngineState state = new();
    static readonly HttpClient Http = CreateHttpClient();
    const string ApiRoot = "https://api.github.com/repos/ggml-org/llama.cpp";
    string StatePath => Path.Combine(dataRoot, "engine-state.json");
    public string Root => string.IsNullOrEmpty(state.Current) ? Path.Combine(appRoot, "runtime", "bundled") : state.Current;
    public string ExperimentalRoot => Path.Combine(appRoot, "runtime", "experimental", "turboquant-cuda-b10726");
    public bool CanRollback => !string.IsNullOrEmpty(state.Previous);
    public EngineManager(string appRoot, string dataRoot)
    {
        this.appRoot = appRoot;
        this.dataRoot = dataRoot;
        if (File.Exists(StatePath))
            try
            {
                state = JsonSerializer.Deserialize<EngineState>(File.ReadAllText(StatePath)) ?? new();
                if (!Directory.Exists(state.Current))
                    state = new();
            }
            catch { state = new(); }
    }
    // Resolves a custom engine ID ("Custom: <name>") to its folder; set by the window from the settings.
    public Func<string, string?>? CustomFolder { get; set; }
    public string RootFor(Profile? profile)
    {
        if (profile != null && CustomEngine.IsCustom(profile.RuntimeId))
        {
            string? folder = CustomFolder?.Invoke(profile.RuntimeId);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException($"The folder of the custom engine \"{CustomEngine.NameOf(profile.RuntimeId)}\" was not found. Add it again on the Engines page.");
            return folder;
        }
        if (profile?.RuntimeId == FlashNextRuntime)
        {
            if (!Directory.Exists(ExperimentalRoot))
                throw new DirectoryNotFoundException("The TurboQuant / Flash-Next experimental runtime is not installed.");
            return ExperimentalRoot;
        }
        return Root;
    }
    readonly Dictionary<string, bool> architectureSupport = new(StringComparer.OrdinalIgnoreCase);
    // llama.dll embeds its architecture-name table as NUL-terminated strings, so an exact byte
    // match is a load-free check that this build implements an architecture.
    public bool BundledSupportsArchitecture(string architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
            return false;
        string key = Root + "|" + architecture;
        if (architectureSupport.TryGetValue(key, out bool known))
            return known;
        bool found = false;
        foreach (string backend in new[] { "cuda", "cpu", "vulkan", "" })
        {
            string dll = Path.Combine(Root, backend, "llama.dll");
            if (!File.Exists(dll))
                continue;
            byte[] needle = System.Text.Encoding.ASCII.GetBytes("\0" + architecture + "\0");
            found = File.ReadAllBytes(dll).AsSpan().IndexOf(needle) >= 0;
            break;
        }
        return architectureSupport[key] = found;
    }
    // Only the measured Flash-Next Q4 layout, or an architecture the bundled engine lacks, is pinned
    // to the experimental runtime. Other qwen4exp models (e.g. a new Qwen 4 release) follow upstream,
    // which the Updates button keeps current; the pinned fork is never upgraded.
    public bool RequiresExperimentalRuntime(ModelInfo model) =>
        model.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)
        && (model.UsesValidatedFlashNextPreset || !BundledSupportsArchitecture(model.Architecture));
    public bool EnsureCompatibleProfile(Profile profile, ModelInfo model)
    {
        // PolyStrata has its own architecture support and must never be redirected
        // based on the capabilities of the bundled llama.cpp engine.
        // A custom engine is the user's explicit choice of build, so it is never redirected either.
        if (profile.RuntimeId == PolyStrata.Runtime || profile.RuntimeId == Basalt.Runtime || CustomEngine.IsCustom(profile.RuntimeId))
            return false;
        if (!RequiresExperimentalRuntime(model))
            return false;
        bool changed = false;
        if (profile.RuntimeId != FlashNextRuntime)
        {
            if (!Directory.Exists(ExperimentalRoot))
                throw new DirectoryNotFoundException("This Flash-Next model requires the compatible qwen4exp CUDA runtime, but that runtime is not installed.");
            profile.RuntimeId = FlashNextRuntime;
            changed = true;
        }
        if (model.UsesValidatedFlashNextPreset && string.IsNullOrWhiteSpace(profile.DraftModel))
        {
            string? modelFolder = Path.GetDirectoryName(profile.Path);
            string candidate = Path.Combine(modelFolder ?? "", "MTP", FlashNextMtpFile);
            if (File.Exists(candidate) && new FileInfo(candidate).Length == 2_622_313_344L)
            {
                profile.DraftModel = candidate;
                profile.Options["MtpDraftMax"] = 2;
                profile.Options["MtpDraftPMin"] = 0.75;
                if (profile.Acceleration == "Force MTP (Experimental)")
                    profile.Acceleration = "On";
                changed = true;
            }
        }
        return changed;
    }
    public string Executable(string backend, Profile? profile = null)
    {
        string root = RootFor(profile);
        string direct = Path.Combine(root, "llama-server.exe");
        return File.Exists(direct) ? direct : Path.Combine(root, backend.ToLowerInvariant(), "llama-server.exe");
    }
    public static async Task<string> Run(string exe, string arguments, int timeout = 20000)
    {
        using var p = new Process { StartInfo = new(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)! } };
        p.Start();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var c = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(c.Token);
        }
        catch { if (!p.HasExited) p.Kill(true); throw new TimeoutException("Engine probe timed out"); }
        var text = await output + "\n" + await error;
        if (p.ExitCode != 0)
            throw new InvalidOperationException("Engine probe failed (exit " + p.ExitCode + "). " + text[..Math.Min(5000, text.Length)]);
        return text;
    }
    public async Task<Probe> Inspect(string backend, Profile? profile = null)
    {
        string exe = Executable(backend, profile);
        return new(await Run(exe, "--version"), await Run(exe, "--help"), await Run(exe, "--list-devices"));
    }
    public async Task<string> Resolve(string preference, Hardware h, Profile? profile = null)
    {
        if (preference != "Auto")
        {
            await Inspect(preference, profile);
            return preference;
        }
        if (h.Gpus.Any(g => g.Vendor == "NVIDIA"))
            try
            {
                if ((await Run(Executable("CUDA", profile), "--list-devices")).Contains("CUDA0"))
                    return "CUDA";
            }
            catch { }
        if (h.Gpus.Count > 0)
            try
            {
                if ((await Run(Executable("Vulkan", profile), "--list-devices")).Contains("Vulkan"))
                    return "Vulkan";
            }
            catch { }
        await Run(Executable("CPU", profile), "--version");
        return "CPU";
    }
    void Save(EngineState next)
    {
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(next));
        if (File.Exists(StatePath))
            File.Replace(tmp, StatePath, StatePath + ".bak");
        else
            File.Move(tmp, StatePath);
        state = next;
    }
    public Task Stage(string folder, string backend) => Stage(folder, backend, null);
    public async Task Stage(string folder, string backend, string? expectedBuildTag)
    {
        // Offline engine bundles require a manifest of all engine files. No untrusted ZIP extraction.
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "manifest.json"))) ?? throw new InvalidDataException("Missing engine manifest");
        var dest = Path.Combine(dataRoot, "engines", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            await Task.Run(() =>
            {
                foreach (var kv in manifest)
                {
                    var rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                    var source = Path.GetFullPath(Path.Combine(folder, rel));
                    var target = Path.GetFullPath(Path.Combine(dest, rel));
                    if (Path.IsPathRooted(rel) || !source.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !target.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsafe engine bundle path");
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Links are not allowed in engine bundles");
                    using var input = File.OpenRead(source);
                    var hash = Convert.ToHexString(SHA256.HashData(input));
                    if (!hash.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Engine bundle hash mismatch");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
            });
            string exe = Path.Combine(dest, backend.ToLowerInvariant(), "llama-server.exe");
            string[] targets = expectedBuildTag == null
                ? new[] { backend, "CPU" }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : new[] { "CPU", "CUDA", "Vulkan" };
            foreach (string targetBackend in targets)
            {
                string candidate = Path.Combine(dest, targetBackend.ToLowerInvariant(), "llama-server.exe");
                if (!File.Exists(candidate))
                    throw new InvalidDataException($"The staged bundle is missing the {targetBackend} llama-server runtime.");
                var candidateVersion = await Run(candidate, "--version");
                if (!candidateVersion.Contains("version", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The {targetBackend} llama-server version probe failed.");
                if (expectedBuildTag != null
                    && (!TryBuildNumber(candidateVersion, out int actualBuild)
                        || !TryBuildNumber(expectedBuildTag, out int expectedBuild)
                        || actualBuild != expectedBuild))
                    throw new InvalidDataException($"The {targetBackend} engine does not match release {expectedBuildTag}.");
                var candidateHelp = await Run(candidate, "--help");
                if (!candidateHelp.Contains("--api-key") || !candidateHelp.Contains("--alias")
                    || !candidateHelp.Contains("--spec-type") || !candidateHelp.Contains("ngram-mod"))
                    throw new InvalidDataException($"The {targetBackend} engine lacks launcher API or ngram-mod support.");
            }
            await Run(exe, "--list-devices");
            Save(new()
            {
                Current = dest,
                Previous = Root
            });
        }
        catch
        { // Staged files remain isolated for inspection; the active pointer is never touched on failure.
            throw;
        }
    }
    public async Task Rollback(string backend)
    {
        if (!CanRollback)
            throw new InvalidOperationException("No previous engine");
        await Run(Path.Combine(state.Previous, backend.ToLowerInvariant(), "llama-server.exe"), "--version");
        Save(new()
        {
            Current = state.Previous,
            Previous = Root
        });
    }
    static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaLauncher/" + (typeof(EngineManager).Assembly.GetName().Version?.ToString(3) ?? "1"));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
    public static bool TryBuildNumber(string value, out int number)
    {
        var match = Regex.Match(value ?? "", @"\bb(?<tag>\d+)\b|\bbuild\s+(?<build>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string digits = match.Groups["tag"].Success ? match.Groups["tag"].Value : match.Groups["build"].Value;
        if (match.Success && int.TryParse(digits, out number)) return true;
        number = 0;
        return false;
    }
    public async Task<EngineUpdateCheck> CheckUpdates(string backend)
    {
        var release = await LatestWindowsRelease();
        var installed = await Run(Executable(backend), "--version");
        return new(release, installed.Trim());
    }
    async Task<EngineRelease> LatestWindowsRelease()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await Http.GetAsync(ApiRoot + "/releases?per_page=30", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        EngineRelease? best = null;
        int bestNumber = -1;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            string tag = item.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
            if (!TryBuildNumber(tag, out int build) || build <= bestNumber) continue;
            var candidate = ParseWindowsRelease(item);
            if (candidate == null) continue;
            best = candidate;
            bestNumber = build;
        }
        return best ?? throw new InvalidDataException("GitHub has no complete Windows x64 release with CPU, CUDA 13, CUDA runtime, Vulkan, and SHA-256 digests.");
    }
    async Task<EngineRelease> ReleaseByTag(string tag)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string escapedTag = Uri.EscapeDataString(tag);
        using var response = await Http.GetAsync(ApiRoot + "/releases/tags/" + escapedTag, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return ParseWindowsRelease(document.RootElement)
            ?? throw new InvalidDataException("The selected GitHub release no longer has a complete Windows x64 engine bundle.");
    }
    static EngineRelease? ParseWindowsRelease(JsonElement item)
    {
        string tag = item.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
        if (!Regex.IsMatch(tag, @"^b\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return null;
        if (!item.TryGetProperty("assets", out var assetsNode) || assetsNode.ValueKind != JsonValueKind.Array) return null;
        var assets = assetsNode.EnumerateArray().ToArray();
        string cudaPattern = "^llama-" + Regex.Escape(tag) + @"-bin-win-cuda-(13(?:\.\d+)?)-x64\.zip$";
        var cudaMatch = assets.Select(a => (Element: a, Match: Regex.Match(
                a.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "",
                cudaPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            .FirstOrDefault(x => x.Match.Success);
        if (cudaMatch.Element.ValueKind != JsonValueKind.Object) return null;
        string cudaVersion = cudaMatch.Match.Groups[1].Value;
        EngineReleaseAsset? cpu = ReadAsset(assets.FirstOrDefault(a =>
            a.TryGetProperty("name", out var n) && n.GetString() == $"llama-{tag}-bin-win-cpu-x64.zip"));
        EngineReleaseAsset? cuda = ReadAsset(cudaMatch.Element);
        EngineReleaseAsset? cudaRuntime = ReadAsset(assets.FirstOrDefault(a =>
            a.TryGetProperty("name", out var n) && n.GetString() == $"cudart-llama-bin-win-cuda-{cudaVersion}-x64.zip"));
        EngineReleaseAsset? vulkan = ReadAsset(assets.FirstOrDefault(a =>
            a.TryGetProperty("name", out var n) && n.GetString() == $"llama-{tag}-bin-win-vulkan-x64.zip"));
        if (cpu == null || cuda == null || cudaRuntime == null || vulkan == null) return null;
        if (!item.TryGetProperty("html_url", out var htmlNode)
            || !Uri.TryCreate(htmlNode.GetString(), UriKind.Absolute, out var htmlUrl)
            || htmlUrl.Scheme != Uri.UriSchemeHttps || htmlUrl.Host != "github.com") return null;
        DateTimeOffset published = item.TryGetProperty("published_at", out var dateNode)
            && DateTimeOffset.TryParse(dateNode.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue;
        return new(tag, htmlUrl.ToString(), published, cpu, cuda, cudaRuntime, vulkan);
    }
    static EngineReleaseAsset? ReadAsset(JsonElement asset)
    {
        if (asset.ValueKind != JsonValueKind.Object
            || !asset.TryGetProperty("name", out var nameNode)
            || !asset.TryGetProperty("browser_download_url", out var urlNode)
            || !asset.TryGetProperty("size", out var sizeNode)
            || !asset.TryGetProperty("digest", out var digestNode)) return null;
        string name = nameNode.GetString() ?? "";
        string urlText = urlNode.GetString() ?? "";
        string digest = digestNode.GetString() ?? "";
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com"
            || !url.AbsolutePath.StartsWith("/ggml-org/llama.cpp/releases/download/", StringComparison.Ordinal)
            || !Regex.IsMatch(digest, @"^sha256:[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !sizeNode.TryGetInt64(out long size) || size <= 0) return null;
        return new(name, url.ToString(), size, digest[7..].ToUpperInvariant());
    }
    static bool SameRelease(EngineRelease a, EngineRelease b) =>
        a.Tag == b.Tag && a.RequiredAssets.Select(x => (x.Name, x.Url, x.Size, x.Sha256))
            .SequenceEqual(b.RequiredAssets.Select(x => (x.Name, x.Url, x.Size, x.Sha256)));
    public async Task InstallUpdate(EngineUpdateCheck checkedUpdate, string backend,
        IProgress<EngineUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Online llama.cpp updates are available for the Windows x64 launcher only.");
        if (!checkedUpdate.UpdateAvailable)
            throw new InvalidOperationException("The checked upstream build is not newer than the installed engine.");
        var release = await ReleaseByTag(checkedUpdate.Release.Tag);
        if (!SameRelease(checkedUpdate.Release, release))
            throw new InvalidDataException("GitHub release files changed after the update check. Check for updates again before installing.");
        long downloadBytes = release.DownloadBytes;
        string volume = Path.GetPathRoot(Path.GetFullPath(dataRoot))!;
        long initialSpace = checked(downloadBytes + 512L * 1024 * 1024);
        var drive = new DriveInfo(volume);
        if (drive.AvailableFreeSpace < initialSpace)
            throw new IOException($"The download needs at least {initialSpace / 1024d / 1024d / 1024d:0.0} GiB free in {volume}. Free space and try again.");
        string work = Path.Combine(dataRoot, "engine-update-" + Guid.NewGuid().ToString("N"));
        string archiveFolder = Path.Combine(work, "downloads");
        string bundle = Path.Combine(work, "bundle");
        Directory.CreateDirectory(archiveFolder);
        Directory.CreateDirectory(bundle);
        long completedBytes = 0;
        try
        {
            var archives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in release.RequiredAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(archiveFolder, asset.Name);
                await DownloadAsset(asset, path, completedBytes, downloadBytes, progress, cancellationToken);
                completedBytes += asset.Size;
                archives.Add(asset.Name, path);
            }
            long expandedBytes = await Task.Run(() => MeasureBundlePayload(release, archives), cancellationToken);
            long remainingSpace = checked(expandedBytes * 2 + 512L * 1024 * 1024);
            if (drive.AvailableFreeSpace < remainingSpace)
                throw new IOException($"The verified archives need about {remainingSpace / 1024d / 1024d / 1024d:0.0} GiB more free space for extraction, staging, and rollback. The current engine is unchanged; free space and try again.");
            progress?.Report(new("Preparing verified runtime bundle", TotalDownloaded: completedBytes, TotalSize: downloadBytes));
            await Task.Run(() => CreateVerifiedBundle(release, archives, bundle, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Probing and staging update", TotalDownloaded: completedBytes, TotalSize: downloadBytes));
            await Stage(bundle, backend, release.Tag);
            progress?.Report(new("Update staged", TotalDownloaded: completedBytes, TotalSize: downloadBytes));
        }
        finally
        {
            if (Directory.Exists(work))
                try { Directory.Delete(work, true); } catch { }
        }
    }
    static async Task DownloadAsset(EngineReleaseAsset asset, string path, long totalBefore,
        long totalSize, IProgress<EngineUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
            throw new InvalidDataException($"GitHub returned a different size for {asset.Name} than its release metadata.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            downloaded += read;
            if (downloaded > asset.Size)
                throw new InvalidDataException($"GitHub sent more data than expected for {asset.Name}.");
            if (downloaded == asset.Size || downloaded % (8 * 1024 * 1024) < read)
                progress?.Report(new("Downloading", asset.Name, downloaded, asset.Size, totalBefore + downloaded, totalSize));
        }
        if (downloaded != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 or size check failed for {asset.Name}; the active engine was not changed.");
    }
    static void CreateVerifiedBundle(EngineRelease release, IReadOnlyDictionary<string, string> archives,
        string bundle, CancellationToken cancellationToken)
    {
        string cpu = Path.Combine(bundle, "cpu");
        string cuda = Path.Combine(bundle, "cuda");
        string vulkan = Path.Combine(bundle, "vulkan");
        Directory.CreateDirectory(cpu);
        Directory.CreateDirectory(cuda);
        Directory.CreateDirectory(vulkan);
        ExtractRuntimeFiles(archives[release.CudaRuntime.Name], cuda, cancellationToken, requireServer: false);
        ExtractRuntimeFiles(archives[release.Cpu.Name], cpu, cancellationToken, requireServer: true);
        ExtractRuntimeFiles(archives[release.Cuda.Name], cuda, cancellationToken, requireServer: true);
        ExtractRuntimeFiles(archives[release.Vulkan.Name], vulkan, cancellationToken, requireServer: true);
        var manifest = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(bundle, file).Replace('\\', '/');
            using var input = File.OpenRead(file);
            manifest[relative] = Convert.ToHexString(SHA256.HashData(input));
        }
        File.WriteAllText(Path.Combine(bundle, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
    static long MeasureBundlePayload(EngineRelease release, IReadOnlyDictionary<string, string> archives)
    {
        long total = 0;
        foreach (var asset in release.RequiredAssets)
        {
            using var archive = ZipFile.OpenRead(archives[asset.Name]);
            foreach (var entry in archive.Entries)
            {
                string normalized = entry.FullName.Replace('\\', '/');
                string[] segments = normalized.Split('/');
                if (normalized.StartsWith('/') || Path.IsPathRooted(normalized)
                    || segments.Any(s => s is ".." or "." || s.Contains(':')))
                    throw new InvalidDataException("The engine archive contains an unsafe file path.");
                int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000)
                    throw new InvalidDataException("Links are not allowed in engine archives.");
                if (normalized.EndsWith('/') || normalized.Contains('/')) continue;
                bool keep = entry.Name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);
                if (keep) total = checked(total + entry.Length);
            }
        }
        return total;
    }
    static void ExtractRuntimeFiles(string archivePath, string output, CancellationToken cancellationToken, bool requireServer)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        bool foundServer = false;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            if (normalized.StartsWith('/') || Path.IsPathRooted(normalized)
                || segments.Any(s => s is ".." or "." || s.Contains(':')))
                throw new InvalidDataException("The engine archive contains an unsafe file path.");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                throw new InvalidDataException("Links are not allowed in engine archives.");
            if (normalized.EndsWith('/') || normalized.Contains('/')) continue;
            bool server = entry.Name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase);
            bool keep = server || entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || entry.Name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);
            if (!keep) continue;
            string target = Path.Combine(output, entry.Name);
            using var source = entry.Open();
            using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            foundServer |= server;
        }
        if (requireServer && !foundServer)
            throw new InvalidDataException("An official engine archive did not contain llama-server.exe.");
    }
}
