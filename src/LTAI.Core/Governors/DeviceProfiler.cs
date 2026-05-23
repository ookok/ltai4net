using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Governors;

public enum DeviceTier { IoT, Mobile, Laptop, Desktop, Workstation, Server }

public record DeviceProfile
{
    public DeviceTier Tier { get; init; }
    public long TotalMemoryMB { get; init; }
    public long AvailableMemoryMB { get; init; }
    public int CpuCores { get; init; }
    public string OsPlatform { get; init; } = "";
    public string OsArch { get; init; } = "";
    public long FreeStorageGB { get; init; }
    public bool HasGpu { get; init; }
    public string GpuBackend { get; init; } = "";
    public int GpuMemoryMB { get; init; }
    public string PreferredLanguage { get; init; } = "zh";
    public string Scenario { get; init; } = "general"; // general, coding, science, entertainment, office

    public bool CanRunOnnx => OsArch.Contains("64");
    public bool CanRunGguf => TotalMemoryMB >= 512;
    public string PreferredEngine => HasGpu && GpuMemoryMB >= 2048 ? "onnx" :
                                     TotalMemoryMB >= 4096 ? "onnx" : "gguf";
}

public record ModelRecommendation
{
    public LocalModelInfo L0Embedding { get; init; } = null!;
    public LocalModelInfo L1Fast { get; init; } = null!;
    public LocalModelInfo? L2Deep { get; init; }
    public string Reason { get; init; } = "";
}

public static class DeviceProfiler
{
    public static DeviceProfile Profile(ILogger? logger = null, string scenario = "general")
    {
        var totalMem = TotalMemoryMB();
        var availMem = LocalModelRegistry.DetectAvailableMemoryMB();
        var cpuCores = Environment.ProcessorCount;
        var osPlatform = RuntimeInformation.OSDescription;
        var osArch = RuntimeInformation.ProcessArchitecture.ToString();
        var gpu = LTAI.Core.Acceleration.HardwareAcceleration.Instance.DetectGPU();
        var freeStorage = FreeStorageGB();

        var tier = ClassifyTier(totalMem, cpuCores, gpu.Available);

        var profile = new DeviceProfile
        {
            TotalMemoryMB = totalMem, AvailableMemoryMB = availMem,
            CpuCores = cpuCores, OsPlatform = osPlatform, OsArch = osArch,
            FreeStorageGB = freeStorage, HasGpu = gpu.Available,
            GpuBackend = gpu.Backend, GpuMemoryMB = gpu.MemoryMb,
            Scenario = scenario, Tier = tier
        };

        logger?.LogInformation(
            "Device: tier={Tier} ram={RAM}MB/{Avail}MB cpu={Cores} gpu={Gpu} storage={Storage}GB",
            tier, totalMem, availMem, cpuCores, gpu.Available, freeStorage);

        return profile;
    }

    public static ModelRecommendation Recommend(DeviceProfile profile)
    {
        var availableMem = profile.AvailableMemoryMB;
        var l0 = LocalModelRegistry.SelectBestModel(availableMem, ModelLayer.L0, profile.PreferredLanguage, profile.PreferredEngine);
        var l1 = LocalModelRegistry.SelectBestModel(availableMem, ModelLayer.L1, profile.PreferredLanguage, profile.PreferredEngine);
        LocalModelInfo? l2 = null;

        // L2 only if sufficient memory AND scenario demands it
        if (availableMem >= 16384 && (profile.Scenario is "coding" or "science"))
            l2 = LocalModelRegistry.SelectBestModel(availableMem, ModelLayer.L2, profile.PreferredLanguage, "gguf");

        var reasons = new List<string>();
        reasons.Add($"L0: {l0.Name} ({l0.DiskSizeMB}MB, {l0.EngineType})");
        reasons.Add($"L1: {l1.Name} ({l1.DiskSizeMB}MB, {l1.EngineType})");
        if (l2 != null)
            reasons.Add($"L2: {l2.Name} ({l2.DiskSizeMB}MB, {l2.EngineType}) — deep reasoning enabled");
        else
            reasons.Add("L2: disabled (insufficient memory or scenario)");

        return new ModelRecommendation
        {
            L0Embedding = l0, L1Fast = l1, L2Deep = l2,
            Reason = string.Join("; ", reasons)
        };
    }

    private static DeviceTier ClassifyTier(long totalMemMB, int cpuCores, bool hasGpu)
    {
        if (totalMemMB < 512) return DeviceTier.IoT;
        if (totalMemMB < 4096) return DeviceTier.Mobile;
        if (totalMemMB < 8192) return DeviceTier.Laptop;
        if (totalMemMB < 16384 || !hasGpu) return DeviceTier.Desktop;
        if (totalMemMB < 65536) return DeviceTier.Workstation;
        return DeviceTier.Server;
    }

    private static long TotalMemoryMB()
    {
        try
        {
            return LocalModelRegistry.DetectAvailableMemoryMB();
        }
        catch
        {
            return 2048; // conservative default
        }
    }

    private static long FreeStorageGB()
    {
        try
        {
            var root = global::System.IO.Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";
            var drive = new global::System.IO.DriveInfo(root);
            return drive.AvailableFreeSpace / (1024 * 1024 * 1024);
        }
        catch { return 10; }
    }
}
