// Copyright (c) LTAI. All rights reserved.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// Startup service that probes available ONNX execution providers (DML, CUDA, CPU)
/// by checking environment-level hints rather than loading a full model.
/// Sets <see cref="LocalEmbedder.Options.Gpu"/> to the fastest available.
/// Runs once at startup, logs the decision.
///
/// NOTE: No longer loads any ONNX model during probing — model is loaded on first use.
/// </summary>
public sealed class EpProbeService : IHostedService
{
    private readonly ILogger<EpProbeService> _logger;

    public EpProbeService(ILogger<EpProbeService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EpProbeService>.Instance;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (LocalEmbedder.DefaultDisabled)
        {
            _logger.LogInformation("EP probe: skipped (remote API in use, ONNX disabled)");
            return Task.CompletedTask;
        }

        _logger.LogInformation("EP probe: detecting available execution providers...");

        // Use simple environment checks instead of WMI (no extra dependency)
        var best = DetectBestProvider();

        if (!string.IsNullOrEmpty(best))
        {
            LocalEmbedder.Options.Gpu = best;
            _logger.LogInformation("EP probe: selected {EP}", best.ToUpperInvariant());
        }
        else
        {
            _logger.LogInformation("EP probe: no GPU detected, using CPU");
            LocalEmbedder.Options.Gpu = "cpu";
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Detect the best execution provider using lightweight environment checks.
    /// Order: DirectML (Windows) > CUDA (NVIDIA) > CPU
    /// </summary>
    private static string? DetectBestProvider()
    {
        // DirectML is available on Windows 10+ (fallback behavior in ONNX Runtime)
        if (OperatingSystem.IsWindows())
        {
            // On Windows, DirectML is always available as long as ONNX Runtime
            // with DirectML support is installed. We detect if there's likely a GPU
            // by checking for NVIDIA/AMD Intel GPU driver DLLs.
            bool likelyHasGpu = false;
            try
            {
                // Try to load DirectML DLL — if it's present, DML is available
                likelyHasGpu = File.Exists(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "DirectML.dll"));
            }
            catch
            {
                // Best-effort
            }

            if (likelyHasGpu)
                return "dml";
        }

        // CUDA check via environment variable (fast, no model loading)
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cudaPath) && Directory.Exists(cudaPath))
        {
            return "cuda";
        }

        return null; // CPU is the default fallback
    }
}
