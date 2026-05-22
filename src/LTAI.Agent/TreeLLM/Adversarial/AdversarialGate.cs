using System.Text.RegularExpressions;
using LTAI.Agent.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Adversarial;

public sealed class AdversarialGate
{
    private readonly ILogger<AdversarialGate> _logger;
    private int _reviews;
    private int _flags;
    private readonly int _maxReviewTokens = 80;

    public AdversarialGate(ILogger<AdversarialGate> logger)
    {
        _logger = logger;
    }

    public double FlagRate => _reviews > 0 ? (double)_flags / _reviews : 0.0;

    public async Task<ReviewResult> Review(
        string response,
        string query,
        Func<string, string, Task<string>> chatFn,
        string systemContext = "")
    {
        Interlocked.Increment(ref _reviews);

        var prompt = $@"You are an adversarial quality reviewer. Review the following response for these issues:
1. Hallucination: statements not supported by facts
2. Contradiction: internal logical conflicts
3. Incompleteness: missing key aspects of the query
4. Overconfidence: making claims without adequate qualification

Original Query: {query}
Response to Review: {response}

{(!string.IsNullOrWhiteSpace(systemContext) ? $"Context: {systemContext}" : "")}

Reply with exactly PASS if no issues found.
Reply with FLAG: <reason> if any issues are found. Keep the reason concise (under {_maxReviewTokens} tokens).";

        try
        {
            var reviewResponse = await chatFn(prompt, systemContext);
            if (string.IsNullOrWhiteSpace(reviewResponse))
            {
                _logger.LogWarning("AdversarialGate: empty review response, passing through");
                return new ReviewResult { Passed = true, Reason = "Empty review response", Confidence = 0.5 };
            }

            var trimmed = reviewResponse.Trim();

            if (trimmed.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
            {
                return new ReviewResult { Passed = true, Reason = "", Confidence = 0.9 };
            }

            if (trimmed.StartsWith("FLAG", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _flags);

                var reason = trimmed.Length > 5 ? trimmed.Substring(4).TrimStart(':', ' ').Trim() : "Unspecified issue";
                _logger.LogWarning("AdversarialGate flagged: {Reason}", reason);

                return new ReviewResult { Passed = false, Reason = reason, Confidence = 0.7 };
            }

            _logger.LogWarning("AdversarialGate: unrecognized review response: {Response}", trimmed);
            return new ReviewResult { Passed = true, Reason = "Unrecognized format", Confidence = 0.5 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AdversarialGate review failed, passing through safely");
            return new ReviewResult { Passed = true, Reason = "Review error - passed through", Confidence = 0.3 };
        }
    }

    public async Task<string> Regenerate(
        string query,
        string originalResponse,
        Func<string, string, Task<string>> chatFn,
        string reviewReason)
    {
        var prompt = $@"Your previous response was flagged for: {reviewReason}

Original Query: {query}
Original Response: {originalResponse}

Please regenerate a corrected response that addresses the issue. Be accurate, complete, and measured in your claims.";

        try
        {
            var regenerated = await chatFn(prompt, "Respond with temp=0.3, be factual and precise.");
            if (string.IsNullOrWhiteSpace(regenerated))
            {
                _logger.LogWarning("AdversarialGate: empty regeneration, falling back to original");
                return originalResponse;
            }

            return regenerated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AdversarialGate regeneration failed, falling back to original");
            return originalResponse;
        }
    }
}
