using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace LlamaLauncher;
public sealed record Gpu(string Name, string Vendor, double TotalGiB, double AvailableGiB, bool Estimated);
public sealed class Hardware
{
    public string Cpu { get; set; } = "CPU";
    public int PhysicalCores { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int LogicalCores { get; set; } = Environment.ProcessorCount;
    public double RamGiB
    {
        get; set;
    }
    public double AvailableRamGiB
    {
        get; set;
    }
    public List<Gpu> Gpus { get; set; } = new();
    [StructLayout(LayoutKind.Sequential)]
    class Memory
    {
        public uint Length = (uint)Marshal.SizeOf<Memory>(); public uint Load; public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] Memory status);
    public static Hardware Detect()
    {
        var h = new Hardware();
        var mem = new Memory();
        if (GlobalMemoryStatusEx(mem))
        {
            h.RamGiB = mem.Total / 1073741824d;
            h.AvailableRamGiB = mem.Available / 1073741824d;
        }
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var item in q.Get())
            {
                h.Cpu = item["Name"]?.ToString()?.Trim() ?? "CPU";
                h.PhysicalCores = Convert.ToInt32(item["NumberOfCores"]);
                h.LogicalCores = Convert.ToInt32(item["NumberOfLogicalProcessors"]);
                break;
            }
        }
        catch { }
        h.Gpus = Dxgi.Read();
        try
        {
            var start = new ProcessStartInfo("nvidia-smi.exe", "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (p.WaitForExit(4000) && p.ExitCode == 0)
                foreach (var line in output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var v = line.Split(',');
                    if (v.Length == 3 && double.TryParse(v[1].Trim(), out var total) && double.TryParse(v[2].Trim(), out var free))
                    {
                        h.Gpus.RemoveAll(g => g.Name.Contains(v[0].Trim(), StringComparison.OrdinalIgnoreCase));
                        h.Gpus.Insert(0, new(v[0].Trim(), "NVIDIA", total / 1024, free / 1024, false));
                    }
                }
            else if (!p.HasExited)
                p.Kill();
        }
        catch { }
        return h;
    }
    public string Summary => $"{string.Join("  •  ", Gpus.Select(g => $"{g.Name}  ·  {g.TotalGiB:0.#} GB VRAM"))}\n{Cpu}  ·  {PhysicalCores}C / {LogicalCores}T  ·  {RamGiB:0} GB RAM ({AvailableRamGiB:0} GB available)";
}
static class Dxgi
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid guid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapter(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc(IntPtr self, out Desc desc);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Desc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name; public uint Vendor, Device, Subsys, Revision; public UIntPtr DedicatedVideo, DedicatedSystem, Shared; public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    static T Method<T>(IntPtr obj, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    public static List<Gpu> Read()
    {
        var result = new List<Gpu>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out factory) < 0)
                return result;
            for (uint i = 0; i < 32; i++)
            {
                if (Method<EnumAdapter>(factory, 12)(factory, i, out var adapter) < 0)
                    break;
                try
                {
                    if (Method<GetDesc>(adapter, 10)(adapter, out var d) >= 0 && (d.Flags & 2) == 0)
                    {
                        double total = d.DedicatedVideo.ToUInt64() / 1073741824d;
                        if (total > 0.1)
                            result.Add(new(d.Name, d.Vendor switch
                            {
                                0x10de => "NVIDIA",
                                0x1002 => "AMD",
                                0x8086 => "Intel",
                                _ => "Other"
                            }, total, Math.Max(0, total - 1.5), true));
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch { }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        return result;
    }
}
public sealed record Estimate(
    double Vram,
    double Ram,
    double Budget,
    double Headroom,
    int Recommended,
    string State,
    bool Approximate,
    double AvailableVram,
    double AvailableRam,
    double VramReserve,
    double RamReserve,
    double VramMargin,
    double RamMargin,
    bool UsesGpu,
    bool OverBudget,
    double ModelVram,
    double KvVram,
    double RuntimeVram);
public static class MemoryEstimator
{
    static double Factor(string k) => k.ToUpperInvariant() switch { "F32" => 4, "F16" or "BF16" => 2, "Q8_0" => 1.0625, "Q5_1" => 0.75, "Q5_0" => 0.6875, "Q4_1" => 0.625, "TURBO4" => 66d / 128, "TURBO3" => 50d / 128, _ => 0.5625 };
    public static Estimate Calculate(ModelInfo m, Profile p, Hardware h, string backend)
    {
        var o = p.Options;
        var policy = MemoryPolicies.Resolve(p);
        bool gpu = backend != "CPU" && h.Gpus.Count > 0 && o.S("GpuLayers") != "0";
        var g = h.Gpus.FirstOrDefault();
        double layers = m.Layers > 0 ? m.Layers : 32;
        double fraction = gpu ? (o.S("GpuLayers").Equals("ALL", StringComparison.OrdinalIgnoreCase) ? 1 : Math.Clamp(o.I("GpuLayers") / layers, 0, 1)) : 0;
        int attn = m.FullAttentionInterval > 0 ? (int)Math.Ceiling(layers / m.FullAttentionInterval) : (int)layers;
        bool uncertain = m.KvHeads <= 0 || m.KeyLength <= 0 || m.ValueLength <= 0;
        var cache = Arguments.EffectiveCache(p, m);
        double perToken = uncertain ? 0.0005 : attn * m.KvHeads * (m.KeyLength * Factor(cache.K) + m.ValueLength * Factor(cache.V)) / 1073741824d;
        double buffers = 1.25 + o.I("UBatchSize", 128) / 2048d + (Arguments.MtpActive(p, m) ? 1 : 0.2);
        var projector = o.S("Mmproj");
        if (File.Exists(projector))
            buffers += new FileInfo(projector).Length / 1073741824d;
        double weights = m.GiB * 1.05, ctx = o.I("ContextSize", 4096), kv = ctx * perToken;
        bool cpuMoe = gpu && o.B("CpuMoe");
        // CPU-MoE keeps the large expert matrices memory-mapped in system memory while
        // the pinned runtime adaptively caches hot experts in otherwise spare VRAM.
        // Resident pages are workload-dependent, so report this path as approximate.
        bool flashNextHybrid = cpuMoe && m.UsesValidatedFlashNextPreset
            && o.I("CpuMoeLayers", (int)layers) > 0 && o.I("CpuMoeLayers", (int)layers) < layers;
        int cpuExpertLayers = Math.Clamp(o.I("CpuMoeLayers", (int)layers), 0, (int)layers);
        // Measured native buffer breakdown: dense CUDA weights plus roughly
        // 1.5 GiB per expert layer moved out of the mmap-backed CPU store.
        double gpuWeights = flashNextHybrid
            ? weights * (0.043 + 0.0135 * (layers - cpuExpertLayers))
            : cpuMoe ? weights * fraction * 0.06 : weights * fraction;
        if ((m.HasPle || m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)) && !m.UsesValidatedFlashNextPreset && cpuMoe)
            gpuWeights = weights * fraction;
        double ramWeights = cpuMoe ? weights * (1 - fraction + 0.30 * fraction) : weights * (1 - fraction);
        if ((m.HasPle || m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)) && !m.UsesValidatedFlashNextPreset && cpuMoe)
            ramWeights = weights;
        double modelVram = gpu ? gpuWeights : 0;
        double kvVram = gpu ? kv * fraction : 0;
        double runtimeVram = gpu ? buffers + (flashNextHybrid && Arguments.MtpActive(p, m) ? 2.2 : 0) : 0;
        double vram = modelVram + kvVram + runtimeVram;
        double configuredWorkingSet = flashNextHybrid ? o.I("WorkingSetLimitGiB", 65) : 0;
        double ram = configuredWorkingSet > 0 ? configuredWorkingSet
            : ramWeights + buffers + kv * (1 - fraction) + Math.Min(weights, 2.0) + o.I("PromptCacheMiB") / 1024d;
        double availableVram = gpu ? Math.Max(0, g?.AvailableGiB ?? 0) : 0;
        double availableRam = Math.Max(0, h.AvailableRamGiB);
        double vBudget = gpu ? Math.Max(0, availableVram - policy.VramReserveGiB) : 0;
        double rBudget = Math.Max(0, availableRam - policy.RamReserveGiB);
        double vLimit = gpu && fraction > 0 ? (vBudget - gpuWeights - buffers) / (perToken * fraction) : double.MaxValue;
        double rLimit = fraction < 1 ? (rBudget - ramWeights - buffers - Math.Min(weights, 2)) / (perToken * (1 - fraction)) : double.MaxValue;
        double native = m.NativeContext > 0 ? m.NativeContext : 32768;
        int recommended = (int)Math.Clamp(Math.Floor(Math.Min(native, Math.Min(vLimit, rLimit)) / 256) * 256, 256, Math.Min(native, 1048576));
        double vramMargin = gpu ? vBudget - vram : double.PositiveInfinity;
        double ramMargin = rBudget - ram;
        bool overBudget = (gpu && vramMargin < 0) || ramMargin < 0;
        bool approximate = uncertain || cpuMoe || m.HasPle || m.HasHybridState || (gpu && (g?.Estimated ?? true));
        bool tight = (gpu && vramMargin < policy.TightVramMarginGiB) || ramMargin < policy.TightRamMarginGiB;
        string state = overBudget ? "Likely to exceed memory" : approximate ? "Estimate uncertain" : tight ? "Tight" : "Comfortable";
        double head = gpu ? Math.Min(vramMargin, ramMargin) : ramMargin;
        return new(vram, ram, gpu ? availableVram : availableRam, head, recommended, state, approximate,
            availableVram, availableRam, policy.VramReserveGiB, policy.RamReserveGiB,
            vramMargin, ramMargin, gpu, overBudget, modelVram, kvVram, runtimeVram);
    }
    public static void Optimize(Profile p, ModelInfo m, Hardware h, string backend)
    {
        // No validated placement recipe exists for a future learned-table architecture.
        // Keep its selected placement/cache/batches and require runtime allocation evidence.
        if ((m.HasPle || m.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)) && !m.UsesValidatedFlashNextPreset)
            return;
        p.Options["CacheK"] = "Q8_0";
        p.Options["CacheV"] = "Q8_0";
        p.Options["Threads"] = h.PhysicalCores;
        p.Options["ThreadsBatch"] = h.PhysicalCores;
        bool flashNext = m.UsesValidatedFlashNextPreset;
        var availableGpu = backend == "CPU" ? null : h.Gpus.FirstOrDefault();
        // Batch is a logical server limit; UBatch controls the physical compute
        // graph and its scratch allocation. Keep the measured Flash-Next path,
        // and give ordinary CUDA models larger prompt batches when weights fit.
        bool roomyCuda = backend == "CUDA" && availableGpu != null
            && availableGpu.AvailableGiB >= 24 && m.GiB * 1.05 + 6 <= availableGpu.AvailableGiB;
        bool moderateGpu = backend != "CPU" && availableGpu != null
            && m.GiB * 1.05 + 4 <= availableGpu.AvailableGiB;
        p.Options["BatchSize"] = flashNext ? 512 : roomyCuda ? 4096 : moderateGpu ? 2048 : 512;
        p.Options["UBatchSize"] = flashNext ? 512 : roomyCuda ? 1024 : moderateGpu ? 512 : 128;
        var g = h.Gpus.FirstOrDefault();
        int layers = m.Layers > 0 ? m.Layers : 32;
        bool cpuMoe = backend != "CPU" && g != null && m.UsesValidatedFlashNextPreset && m.GiB > g.AvailableGiB * 2;
        p.Options["CpuMoe"] = cpuMoe;
        if (cpuMoe)
        {
            p.Options["CpuMoeLayers"] = 36;
            p.Options["MoeCacheEnabled"] = false;
            p.Options["WorkingSetLimitGiB"] = 65;
            p.Options["PromptCacheMiB"] = 2048;
        }
        int fit = backend == "CPU" || g == null ? 0 : (int)Math.Clamp(Math.Floor((g.AvailableGiB - 3) / (Math.Max(0.1, m.GiB) * 1.05) * layers), 0, layers);
        p.Options["GpuLayers"] = cpuMoe || fit == layers ? "ALL" : fit.ToString();
        // The validated Flash-Next CUDA/CPU-MoE path has a useful 64K starting
        // point. Retain the conservative 32K cap for model families that have
        // not passed that larger launcher-path allocation.
        int optimizedContextCap = m.UsesValidatedFlashNextPreset ? 65536 : 32768;
        p.Options["ContextSize"] = Math.Min(optimizedContextCap, Calculate(m, p, h, backend).Recommended);
        p.Options["FlashAttention"] = true;
    }
}
