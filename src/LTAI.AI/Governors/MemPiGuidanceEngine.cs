using LTAI.Core.Governors;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Governors;

// ============================================================================
// Mem-π: Adaptive Memory through Learning When and What to Generate
// Based on arXiv:2605.21463 — ServiceNow AI Research / UMontreal / McGill / CIFAR
//
// Key innovation over retrieval-based memory:
//   1. Joint WHEN+WHAT decision — model decides to generate OR abstain
//   2. Generated guidance is context-specific, not static retrieval
//   3. Abstain path avoids injecting irrelevant noise into the agent pipeline
// ============================================================================

/// <summary>
/// Result of a Mem-π guidance generation call.
/// </summary>
public sealed record MemPiResult
{
    /// <summary>True if the model decided to generate guidance.</summary>
    public bool Generated { get; init; }

    /// <summary>Guidance text. Null if abstained.</summary>
    public string? Guidance { get; init; }

    /// <summary>Confidence score [0,1] from the decision head.</summary>
    public float Confidence { get; init; }

    /// <summary>Latency in milliseconds.</summary>
    public long LatencyMs { get; init; }

    /// <summary>Model that produced this result.</summary>
    public string ModelName { get; init; } = "";

    /// <summary>Reason for abstention, if applicable.</summary>
    public string? AbstainReason { get; init; }

    public static MemPiResult Abstain(float confidence, string reason, long latencyMs, string model)
        => new() { Generated = false, Confidence = confidence, AbstainReason = reason, LatencyMs = latencyMs, ModelName = model };

    public static MemPiResult Generate(string guidance, float confidence, long latencyMs, string model)
        => new() { Generated = true, Guidance = guidance, Confidence = confidence, LatencyMs = latencyMs, ModelName = model };

    public static MemPiResult NotReady => new() { Generated = false, AbstainReason = "Model not ready" };
}

/// <summary>
/// Mem-π Guidance Engine: wraps a local ONNX LLM to provide
/// context-specific guidance with joint generate/abstain decision.
///
/// Usage:
///   var result = await engine.GenerateGuidanceAsync(
///       sessionContext: "User is debugging a C# null reference...",
///       query: "fix NRE in AgentFactory.cs line 42",
///       ct);
///   if (result.Generated)
///       agentContext += result.Guidance;  // inject guidance into prompt
/// </summary>
public sealed class MemPiGuidanceEngine : IMemPiGuidance, IDisposable
{
    private readonly ILocalLlmEngine _llm;
    private readonly ILogger<MemPiGuidanceEngine> _logger;
    private readonly MemPiConfig _config;
    private readonly Dictionary<string, int> _abstainHistory = new(); // track per-context abstain counts
    private int _totalCalls;
    private int _totalGenerated;
    private int _totalAbstained;

    public int TotalCalls => _totalCalls;
    public int TotalGenerated => _totalGenerated;
    public int TotalAbstained => _totalAbstained;
    public double GenerateRate => _totalCalls > 0 ? (double)_totalGenerated / _totalCalls : 0;
    public bool IsAvailable => _llm.IsReady;

    public MemPiGuidanceEngine(
        ILocalLlmEngine llm,
        MemPiConfig? config = null,
        ILogger<MemPiGuidanceEngine>? logger = null)
    {
        _llm = llm;
        _config = config ?? new MemPiConfig();
        _logger = logger ?? NullLogger<MemPiGuidanceEngine>.Instance;
    }

    /// <summary>
    /// Generate context-specific guidance, or abstain if not needed.
    /// This is the core Mem-π inference method.
    /// </summary>
    public async Task<MemPiResult> GenerateGuidanceAsync(
        string sessionContext,
        string query,
        CancellationToken ct = default)
    {
        if (!_llm.IsReady)
            return MemPiResult.NotReady;

        Interlocked.Increment(ref _totalCalls);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Build the Mem-π prompt: joint WHEN + WHAT decision
            var prompt = BuildMemPiPrompt(sessionContext, query);

            // Generate with low temperature for deterministic abstain decisions
            var response = await _llm.GenerateAsync(
                prompt,
                temperature: _config.Temperature,
                maxTokens: _config.MaxGuidanceTokens,
                ct).ConfigureAwait(false);

            sw.Stop();

            // Parse the response: check for abstain marker
            var (generated, guidance, confidence) = ParseMemPiResponse(response);

            if (generated && !string.IsNullOrWhiteSpace(guidance))
            {
                Interlocked.Increment(ref _totalGenerated);
                return MemPiResult.Generate(guidance, confidence, sw.ElapsedMilliseconds, _llm.ModelName);
            }

            // Abstain: track context to avoid repeated unnecessary calls
            var contextKey = ComputeContextHash(sessionContext);
            lock (_abstainHistory)
            {
                _abstainHistory.TryGetValue(contextKey, out var count);
                _abstainHistory[contextKey] = count + 1;
            }

            Interlocked.Increment(ref _totalAbstained);
            var reason = response?.Length < 10
                ? "model abstained (short response)"
                : $"model abstained: {response?[..Math.Min(50, response.Length)]}";
            return MemPiResult.Abstain(confidence, reason, sw.ElapsedMilliseconds, _llm.ModelName);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "MemPiGuidanceEngine: generation failed, abstaining");
            Interlocked.Increment(ref _totalAbstained);
            return MemPiResult.Abstain(0f, $"error: {ex.Message}", sw.ElapsedMilliseconds, _llm.ModelName);
        }
    }

    /// <summary>
    /// Check if we should attempt guidance for this context.
    /// Avoids calling the model when we've recently abstained for similar context.
    /// </summary>
    // IMemPiGuidance explicit implementation
    async Task<MemPiBridgeResult> IMemPiGuidance.GenerateGuidanceAsync(
        string sessionContext, string query, CancellationToken ct)
    {
        var result = await GenerateGuidanceAsync(sessionContext, query, ct).ConfigureAwait(false);
        return new MemPiBridgeResult
        {
            Generated = result.Generated,
            Guidance = result.Guidance,
            Confidence = result.Confidence,
            LatencyMs = result.LatencyMs,
            ModelName = result.ModelName,
            AbstainReason = result.AbstainReason
        };
    }

    public bool ShouldAttemptGuidance(string sessionContext)
    {
        var key = ComputeContextHash(sessionContext);
        lock (_abstainHistory)
        {
            if (_abstainHistory.TryGetValue(key, out var count) && count >= _config.MaxAbstainsBeforeSkip)
            {
                _logger.LogDebug("MemPi: skipping guidance — abstained {Count}x for context {Hash}", count, key[..8]);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Build the Mem-π decision prompt. This is the core prompt engineering
    /// that implements the joint WHEN+WHAT decision from the paper.
    /// </summary>
    private string BuildMemPiPrompt(string context, string query)
    {
        return $"""
            [MEM-π: Adaptive Memory Guidance]
            You are a lightweight guidance model. Your job: decide whether the following agent context needs helpful guidance, and if so, provide it concisely.

            RULES:
            1. If the agent clearly knows what to do (simple greeting, trivial arithmetic, known fact), output: [ABSTAIN]
            2. If the context suggests the agent might benefit from a hint (complex task, debugging, design, uncertainty), output: [GUIDANCE] followed by 1-3 sentences of concise, actionable advice.
            3. Never repeat the query. Never apologize. Never explain your decision.
            4. Guidance must be NEW information not already present in the context.

            CONTEXT: {Truncate(context, _config.MaxContextChars)}
            QUERY: {Truncate(query, _config.MaxQueryChars)}

            DECISION:
            """;
    }

    /// <summary>
    /// Parse the model response to extract the decision and guidance.
    /// </summary>
    private (bool generated, string? guidance, float confidence) ParseMemPiResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return (false, null, 0f);

        var trimmed = response.Trim();

        // Explicit abstain
        if (trimmed.StartsWith("[ABSTAIN]", StringComparison.OrdinalIgnoreCase))
            return (false, null, 0.9f);

        // Check for abstain keywords
        var lower = trimmed.ToLowerInvariant();
        if (lower is "abstain" or "no" or "none" or "skip" or "不需要" or "无需" or "跳过")
            return (false, null, 0.85f);

        // Explicit guidance
        if (trimmed.StartsWith("[GUIDANCE]", StringComparison.OrdinalIgnoreCase))
        {
            var guidance = trimmed["[GUIDANCE]".Length..].Trim();
            return string.IsNullOrWhiteSpace(guidance)
                ? (false, null, 0.5f)
                : (true, guidance, 0.8f);
        }

        // Heuristic: short responses (<20 chars) that aren't guidance → abstain
        if (trimmed.Length < 20 && !trimmed.Contains(' ') && !trimmed.Contains('\n'))
            return (false, null, 0.6f);

        // Otherwise treat as guidance
        return (true, trimmed, 0.7f);
    }

    private static string ComputeContextHash(string context)
    {
        if (string.IsNullOrEmpty(context)) return "empty";
        // Simple FNV-1a hash for deduplication
        uint hash = 2166136261;
        foreach (var c in context.Take(200))
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash.ToString("x8");
    }

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "...";

    public void Dispose() => _llm?.Dispose();
}

/// <summary>
/// Configuration for the Mem-π guidance engine.
/// </summary>
public sealed record MemPiConfig
{
    /// <summary>Temperature for generation. Lower = more deterministic abstain.</summary>
    public float Temperature { get; init; } = 0.3f;

    /// <summary>Max tokens for generated guidance.</summary>
    public int MaxGuidanceTokens { get; init; } = 64;

    /// <summary>Max context characters fed to the prompt.</summary>
    public int MaxContextChars { get; init; } = 800;

    /// <summary>Max query characters fed to the prompt.</summary>
    public int MaxQueryChars { get; init; } = 300;

    /// <summary>Skip guidance attempts after N consecutive abstains for same context.</summary>
    public int MaxAbstainsBeforeSkip { get; init; } = 3;
}
