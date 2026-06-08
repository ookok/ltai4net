// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PcaProjectorFactory — creates IPcaProjector from config
//
//  Phase 1b: reads LTAI:Vector:Reduction config to select and
//  instantiate the appropriate IPcaProjector.
//
//  Config values:
//    "none"       — no reduction (returns null)
//    "pca-128"    — 384 → 128 via RandomPca (cold-start)
//    "pca-64"     — 384 → 64  via RandomPca (cold-start)
//    "pca-trained-128" — 384 → 128 via TrainedPca (requires Fit())
// ═══════════════════════════════════════════════════════════════

using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.DimReduction;

/// <summary>
/// Creates IPcaProjector instances based on a reduction config string.
/// </summary>
public static class PcaProjectorFactory
{
    /// <summary>
    /// Parse the reduction setting and create the corresponding projector.
    /// </summary>
    /// <param name="reductionConfig">Value of LTAI:Vector:Reduction.</param>
    /// <param name="inputDim">Source dimension (e.g. 384).</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>An IPcaProjector, or null if reduction is "none".</returns>
    public static IPcaProjector? Create(
        string reductionConfig,
        int inputDim = 384,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var config = reductionConfig?.Trim().ToLowerInvariant() ?? "none";

        switch (config)
        {
            case "none":
            case "":
                return null;

            case "pca-128":
                logger.LogInformation("PcaProjectorFactory: creating RandomPca {Input}→128", inputDim);
                return new RandomPca(inputDim, 128);

            case "pca-64":
                logger.LogInformation("PcaProjectorFactory: creating RandomPca {Input}→64", inputDim);
                return new RandomPca(inputDim, 64);

            case "pca-trained-128":
                logger.LogInformation("PcaProjectorFactory: creating TrainedPca {Input}→128 (must call Fit!)", inputDim);
                // TrainedPca requires Fit() — the caller must train it with
                // domain samples before use. We return a zero-filled stub.
                return TrainedPca.Fit(
                    // Single zero vector as placeholder — caller MUST retrain
                    [new float[inputDim]],
                    128);

            default:
                logger.LogWarning("PcaProjectorFactory: unknown reduction '{Config}', using none", config);
                return null;
        }
    }

    /// <summary>
    /// Parse the output dimension from a reduction config string.
    /// </summary>
    public static int GetOutputDim(string reductionConfig)
    {
        return (reductionConfig?.Trim().ToLowerInvariant()) switch
        {
            "pca-128" or "pca-trained-128" => 128,
            "pca-64" => 64,
            _ => -1, // no reduction
        };
    }
}
