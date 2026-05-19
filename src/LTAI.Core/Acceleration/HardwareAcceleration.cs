using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Acceleration;

public record GPUInfo(
    bool Available,
    string Backend,
    string DeviceName,
    int DeviceCount,
    int MemoryMb,
    string ComputeCapability)
{
    public bool CanAccelerateFaiss => Available && (Backend == "cuda" || Backend == "mps");
    public bool CanAccelerateTorch => Available;
    public bool CanAccelerateOcr => Available && Backend == "cuda";

    public static GPUInfo None => new(false, "cpu", "Unavailable", 0, 0, "N/A");
}

public sealed class HardwareAcceleration
{
    private static readonly Lazy<HardwareAcceleration> _instance = new(() => new HardwareAcceleration());
    public static HardwareAcceleration Instance => _instance.Value;

    private readonly ILogger<HardwareAcceleration> _logger;
    private bool _forceCpu;

    public HardwareAcceleration() : this(NullLogger<HardwareAcceleration>.Instance) { }

    public HardwareAcceleration(ILogger<HardwareAcceleration> logger)
    {
        _logger = logger ?? NullLogger<HardwareAcceleration>.Instance;
    }

    public void ForceCpu(bool force = true)
    {
        _forceCpu = force;
        _logger.LogInformation("ForceCpu set to {Force}", force);
    }

    public GPUInfo DetectGPU()
    {
        if (_forceCpu)
            return GPUInfo.None;

        var cudaVisible = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        if (!string.IsNullOrWhiteSpace(cudaVisible) && cudaVisible != "-1")
        {
            var deviceCount = cudaVisible.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
            _logger.LogInformation("CUDA detected via CUDA_VISIBLE_DEVICES: {Devices}", cudaVisible);
            return new GPUInfo(true, "cuda", "NVIDIA GPU (simulated)", deviceCount, 8192, "8.0");
        }

        var rocmVisible = Environment.GetEnvironmentVariable("ROCR_VISIBLE_DEVICES");
        if (!string.IsNullOrWhiteSpace(rocmVisible) && rocmVisible != "-1")
        {
            _logger.LogInformation("ROCm detected via ROCR_VISIBLE_DEVICES: {Devices}", rocmVisible);
            return new GPUInfo(true, "cuda", "AMD GPU (ROCm simulated)", 1, 4096, "gfx1030");
        }

        _logger.LogInformation("No GPU detected, defaulting to CPU");
        return GPUInfo.None;
    }

    public string Report()
    {
        var gpu = DetectGPU();
        var lines = new List<string>
        {
            $"GPU Available: {gpu.Available}",
            $"Backend: {gpu.Backend}",
            $"Device: {gpu.DeviceName}",
            $"Device Count: {gpu.DeviceCount}",
            $"Memory (MB): {gpu.MemoryMb}",
            $"Compute Capability: {gpu.ComputeCapability}",
            $"Can Accelerate FAISS: {gpu.CanAccelerateFaiss}",
            $"Can Accelerate Torch: {gpu.CanAccelerateTorch}",
            $"Can Accelerate OCR: {gpu.CanAccelerateOcr}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    public string GetTorchDevice()
    {
        var gpu = DetectGPU();
        return gpu.Available ? "cuda" : "cpu";
    }

    public List<double[]> BatchEmbed(List<string> texts, int batchSize = 32)
    {
        if (texts == null || texts.Count == 0)
            return new List<double[]>();

        var results = new List<double[]>();
        for (int i = 0; i < texts.Count; i++)
            results.Add(new double[0]);

        _logger.LogInformation("BatchEmbed: {Count} texts processed in batches of {BatchSize} (simulated)", texts.Count, batchSize);
        return results;
    }

    public List<List<T>> ParallelChunk<T>(List<T> documents, int chunkSize = 1000)
    {
        if (documents == null || documents.Count == 0)
            return new List<List<T>>();

        var chunks = new ConcurrentBag<List<T>>();
        var totalChunks = Math.Max(1, (int)Math.Ceiling((double)documents.Count / chunkSize));

        Parallel.ForEach(Enumerable.Range(0, totalChunks), chunkIndex =>
        {
            var start = chunkIndex * chunkSize;
            var end = Math.Min(start + chunkSize, documents.Count);
            var chunk = documents.GetRange(start, end - start);
            chunks.Add(chunk);
        });

        _logger.LogInformation("ParallelChunk: {TotalDocs} documents split into {ChunkCount} chunks", documents.Count, chunks.Count);
        return chunks.OrderBy(c => documents.IndexOf(c[0])).ToList();
    }

    public Dictionary<string, object> Stats()
    {
        var gpu = DetectGPU();
        return new Dictionary<string, object>
        {
            ["available"] = gpu.Available,
            ["backend"] = gpu.Backend,
            ["device_name"] = gpu.DeviceName,
            ["device_count"] = gpu.DeviceCount,
            ["memory_mb"] = gpu.MemoryMb,
            ["compute_capability"] = gpu.ComputeCapability,
            ["can_accelerate_faiss"] = gpu.CanAccelerateFaiss,
            ["can_accelerate_torch"] = gpu.CanAccelerateTorch,
            ["can_accelerate_ocr"] = gpu.CanAccelerateOcr,
            ["force_cpu"] = _forceCpu
        };
    }
}
