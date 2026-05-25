using LTAI.AI.Governors;
using LTAI.AI.Utilities;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed class LogiInputFilter
{
    private readonly ILogger<LogiInputFilter> _logger;

    public LogiInputFilter(ILogger<LogiInputFilter> logger)
    {
        _logger = logger;
    }

    public (ChatMessage enriched, string label, float complexity, string emotion) Analyze(
        ChatMessage message)
    {
        var query = message.Text ?? "";
        var (complexity, label) = GovernorUtilities.ClassifyIntent(query);
        var emotion = GovernorUtilities.DetectEmotion(query);

        _logger.LogInformation("MAF input: label={Label}, complexity={Complexity:F2}, emotion={Emotion}",
            label, complexity, emotion);

        var enrichedText = query;
        if (!string.IsNullOrEmpty(emotion) && emotion != "neutral")
            enrichedText = $"[emotion: {emotion}] {query}";

        return (new ChatMessage(ChatRole.User, enrichedText), label, complexity, emotion);
    }
}

public sealed class LogiOutputFilter
{
    private readonly ILogger<LogiOutputFilter> _logger;
    private int _blocksSinceReset;
    private DateTime _lastBlock;

    public int TotalBlocks => _blocksSinceReset;

    public LogiOutputFilter(ILogger<LogiOutputFilter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Review output and BLOCK if hallucination or quality issues detected.
    /// Previously was a write-only no-op (always returned original text).
    /// Now gates: GovernorUtilities heuristics + HallucinationGuard score.
    /// </summary>
    public (string? AllowedText, string? BlockReason) Review(string responseText)
    {
        if (string.IsNullOrEmpty(responseText))
            return (null, "Empty response");

        // Layer 1: heuristic check via GovernorUtilities
        var (isHallucinated, reason) = GovernorUtilities.CheckHallucination(responseText);
        if (isHallucinated)
        {
            _logger.LogWarning("OutputFilter: heuristic hallucination - {Reason}", reason);
            _blocksSinceReset++;
            _lastBlock = DateTime.UtcNow;
            return (null, $"Hallucination detected: {reason}");
        }

        // Layer 2: HallucinationGuard deep check
        var guardVerdict = HallucinationGuard.Instance.CheckGeneration(responseText);
        if (guardVerdict.Score > 0.7f)
        {
            _logger.LogWarning("OutputFilter: HallucinationGuard score={Score:F2}", guardVerdict.Score);
            _blocksSinceReset++;
            return (null, $"Quality check failed: {guardVerdict.Reason}");
        }

        // Layer 3: basic content validation
        if (responseText.Length < 5)
        {
            _logger.LogWarning("OutputFilter: response too short ({Len} chars)", responseText.Length);
            return (null, "Response too short");
        }

        if (responseText.Contains("I cannot") && responseText.Contains("I apologize") && responseText.Length < 50)
        {
            _logger.LogWarning("OutputFilter: refusal pattern detected");
            return (null, "Model refused to answer");
        }

        return (responseText, null);
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_blocks"] = _blocksSinceReset,
        ["last_block"] = _lastBlock.ToString("O"),
        ["guard_status"] = HallucinationGuard.Instance.GetDashboard()
    };
}
