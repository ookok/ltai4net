using Microsoft.ML.OnnxRuntime;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Acceleration;

public enum GpuBackend { None, DirectML, Cuda, OpenVINO, CoreML }

public sealed class OnnxAccelerator
{
    private readonly ILogger _logger;

    public GpuBackend ActiveBackend { get; private set; } = GpuBackend.None;
    public bool IsGpuAvailable => ActiveBackend != GpuBackend.None;

    public OnnxAccelerator(ILogger<OnnxAccelerator>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxAccelerator>.Instance;
        DetectBackend();
    }

    private void DetectBackend()
    {
        var available = OrtEnv.Instance().GetAvailableProviders();
        _logger.LogInformation("ONNX providers: {Providers}", string.Join(", ", available));

        if (available.Contains("DmlExecutionProvider"))
        {
            ActiveBackend = GpuBackend.DirectML;
            _logger.LogInformation("GPU acceleration: DirectML (Windows)");
        }
        else if (available.Contains("CUDAExecutionProvider"))
        {
            ActiveBackend = GpuBackend.Cuda;
            _logger.LogInformation("GPU acceleration: CUDA (Linux)");
        }
        else if (available.Contains("OpenVINOExecutionProvider"))
        {
            ActiveBackend = GpuBackend.OpenVINO;
            _logger.LogInformation("GPU acceleration: OpenVINO");
        }
        else if (available.Contains("CoreMLExecutionProvider"))
        {
            ActiveBackend = GpuBackend.CoreML;
            _logger.LogInformation("GPU acceleration: CoreML (macOS)");
        }
        else
        {
            _logger.LogInformation("GPU acceleration: not available — using CPU");
        }
    }

    public SessionOptions CreateSessionOptions(bool enableGpu = true)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = true,
            IntraOpNumThreads = Environment.ProcessorCount,
            InterOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };

        if (enableGpu && IsGpuAvailable)
        {
            try
            {
                switch (ActiveBackend)
                {
                    case GpuBackend.DirectML:
                        options.AppendExecutionProvider_DML();
                        break;
                    case GpuBackend.Cuda:
                        options.AppendExecutionProvider_CUDA();
                        break;
                    case GpuBackend.OpenVINO:
                        options.AppendExecutionProvider_OpenVINO();
                        break;
                    case GpuBackend.CoreML:
                        options.AppendExecutionProvider_CoreML(
                            CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH);
                        break;
                }
                _logger.LogDebug("ONNX session: {Backend} execution provider attached", ActiveBackend);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPU provider {Backend} failed to attach — falling back to CPU", ActiveBackend);
                ActiveBackend = GpuBackend.None;
            }
        }

        return options;
    }

    public SessionOptions CreateCpuOnlyOptions()
    {
        return new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = true,
            IntraOpNumThreads = Environment.ProcessorCount,
            InterOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };
    }
}
