using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Experts.Routing;

/// <summary>
/// Executes multiple expert queries in parallel via Task.WhenAll.
/// Each expert receives the same ExpertQuery; results are collected
/// for downstream aggregation.
/// </summary>
public sealed class ParallelFanOutExecutor
{
    private readonly ILogger<ParallelFanOutExecutor>? _logger;

    public ParallelFanOutExecutor(ILogger<ParallelFanOutExecutor>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Query a set of experts in parallel. Individual expert failures are
    /// caught and logged; they produce NoAnswer responses rather than
    /// failing the entire fan-out.
    /// </summary>
    public async Task<IReadOnlyList<ExpertResponse>> ExecuteAsync(
        IReadOnlyList<(IExpertModule Expert, float RouterConfidence)> expertSelections,
        ExpertQuery query,
        CancellationToken ct = default)
    {
        if (expertSelections.Count == 0) return [];

        var tasks = expertSelections.Select(async sel =>
        {
            try
            {
                var response = await sel.Expert.QueryAsync(query, ct).ConfigureAwait(false);
                return response with { Confidence = response.Confidence * sel.RouterConfidence };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "ParallelFanOut: expert {ExpertId} failed", sel.Expert.ExpertId);
                return new ExpertResponse(
                    sel.Expert.ExpertId, string.Empty, 0f, [],
                    new ProvenanceInfo("error", null),
                    NoAnswer: true, ClarifyQuestion: $"Expert {sel.Expert.ExpertId} query failed: {ex.Message}");
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
