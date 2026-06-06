// Copyright (c) LTAI. All rights reserved.

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// Startup service that probes available ONNX execution providers (DML, CUDA, CPU)
/// and sets <see cref="LocalEmbedder.Options.Gpu"/> to the fastest available.
/// Runs once at startup, logs the decision.
/// </summary>
public sealed class EpProbeService : IHostedService
{
    private readonly ILogger<EpProbeService> _logger;

    public EpProbeService(ILogger<EpProbeService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EpProbeService>.Instance;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (LocalEmbedder.DefaultDisabled)
        {
            _logger.LogInformation("EP probe: skipped (remote API in use, ONNX disabled)");
            return;
        }

        _logger.LogInformation("EP probe: scanning available execution providers...");

        var candidates = new[] { "dml", "cuda", "cpu" };
        var results = new List<(string ep, double ms)>();

        foreach (var ep in candidates)
        {
            if (ct.IsCancellationRequested) break;

            LocalEmbedder.Options.Gpu = ep;
            LocalEmbedder.Options.Quantization = "int8";

            try
            {
                using var embedder = new LocalEmbedder();
                if (!embedder.Available)
                {
                    _logger.LogDebug("EP probe: {EP} not available", ep);
                    continue;
                }

                // Quick benchmark: 5 warmup + 10 iterations
                for (int i = 0; i < 5; i++)
                    embedder.Generate("warmup probe");

                var sw = Stopwatch.StartNew();
                var totalMs = 0.0;
                for (int i = 0; i < 10; i++)
                {
                    var t = Stopwatch.StartNew();
                    embedder.Generate("hello world EP probe");
                    t.Stop();
                    totalMs += t.Elapsed.TotalMilliseconds;
                }
                sw.Stop();

                var avg = totalMs / 10;
                results.Add((ep, avg));
                _logger.LogInformation("EP probe: {EP} = {F1}ms avg", ep.ToUpperInvariant(), avg);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EP probe: {EP} failed", ep);
            }
        }

        if (results.Count == 0)
        {
            _logger.LogWarning("EP probe: no execution provider available, fallback to CPU");
            LocalEmbedder.Options.Gpu = "cpu";
            return;
        }

        // Select fastest
        results.Sort((a, b) => a.ms.CompareTo(b.ms));
        var best = results[0];
        LocalEmbedder.Options.Gpu = best.ep;
        _logger.LogInformation(
            "EP probe: selected {EP} ({F1}ms) — set LTAI:Embedding:Gpu to override",
            best.ep.ToUpperInvariant(), best.ms);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
