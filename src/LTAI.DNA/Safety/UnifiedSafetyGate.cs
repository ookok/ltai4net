using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.DNA.Safety;

public enum GateAction { Allow, Warn, Block, BlockWithRedaction }

public sealed class GateVerdict
{
    public GateAction Action { get; init; } = GateAction.Allow;
    public double RiskScore { get; init; }
    public string? Reason { get; init; }
    public bool IsAllowed => Action == GateAction.Allow || Action == GateAction.Warn;

    public static GateVerdict Allow(double riskScore = 0) =>
        new() { Action = GateAction.Allow, RiskScore = riskScore };

    public static GateVerdict Warn(string reason) =>
        new() { Action = GateAction.Warn, Reason = reason };

    public static GateVerdict Block(string reason) =>
        new() { Action = GateAction.Block, Reason = reason };

    public static GateVerdict BlockWithRedaction(string reason) =>
        new() { Action = GateAction.BlockWithRedaction, Reason = reason };
}

public sealed class UnifiedSafetyGate
{
    private readonly ILogger<UnifiedSafetyGate> _logger;
    private readonly SafetyCoordinator _coordinator;
    private readonly PolicyAsCode _policy;
    private readonly float _encodedInjectionRiskThreshold;
    private readonly float _cumulativeRiskThreshold;
    private readonly float _injectionScorePerHit;
    private readonly ConcurrentDictionary<string, SessionSafetyState> _sessions = new();

    private sealed class SessionSafetyState
    {
        public DateTime? FrozenUntil { get; set; }
        public int StrikeCount { get; set; }
        public Queue<double> RiskHistory { get; } = new();
    }

    private static readonly TimeSpan[] CoolingDurations =
    {
        TimeSpan.Zero,
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    };

    private static readonly string[] InjectionKeywords =
    {
        "ignore all previous", "ignore previous instructions", "ignore all instructions",
        "disregard all previous", "forget all previous", "override system prompt",
        "you are now", "act as if", "pretend you are", "new instructions",
        "system prompt", "reveal your instructions", "output your prompt",
        "忽略之前", "忽略所有指令", "忽略以上", "覆盖系统", "无视规则",
        "你现在是", "扮演一个", "假设你是", "输出你的提示词", "显示系统提示"
    };

    private static readonly Regex Base64Pattern = new(
        @"[A-Za-z0-9+/]{20,}={0,2}", RegexOptions.Compiled);

    public UnifiedSafetyGate(
        ILogger<UnifiedSafetyGate> logger,
        SafetyCoordinator coordinator,
        PolicyAsCode policy,
        IOptions<LTAIOptions> options)
    {
        _logger = logger;
        _coordinator = coordinator;
        _policy = policy;
        var t = options.Value.Thresholds;
        _encodedInjectionRiskThreshold = t.EncodedInjectionRiskThreshold;
        _cumulativeRiskThreshold = t.CumulativeRiskThreshold;
        _injectionScorePerHit = t.InjectionScorePerHit;
    }

    public async Task<GateVerdict> EvaluateInputAsync(
        string input, string sessionId, CancellationToken ct = default)
    {
        using var activity = new ActivitySource("LTAI.Safety").StartActivity("safety.evaluate_input");
        activity?.SetTag("safety.session_id", sessionId);
        activity?.SetTag("safety.input_length", input?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(input))
        {
            _logger.LogWarning("SafetyGate: Empty/null input from session {Session}", sessionId);
            return GateVerdict.Block("Empty input detected.");
        }

        if (_sessions.TryGetValue(sessionId, out var session) && session.FrozenUntil.HasValue)
        {
            if (DateTime.UtcNow < session.FrozenUntil.Value)
                return GateVerdict.Block(
                    $"Session frozen until {session.FrozenUntil:HH:mm:ss} (strike {session.StrikeCount}). Please wait.");
            session.FrozenUntil = null;
        }

        var decoded = DecodeEncodings(input);
        if (decoded != input)
        {
            var decodedRisk = await _coordinator.EvaluateAsync(decoded, null, ct);
            if (decodedRisk.RiskScore > _encodedInjectionRiskThreshold)
                return EscalateAndBlock(sessionId, "Encoded injection detected");
        }

        var injectionScore = ComputeInjectionScore(input);

        var verdict = await _coordinator.EvaluateAsync(input, null, ct);

        var cumulative = UpdateCumulativeRisk(sessionId, verdict.RiskScore + injectionScore);
        if (cumulative > _cumulativeRiskThreshold)
        {
            activity?.SetTag("safety.block_reason", "cumulative_risk");
            activity?.SetTag("safety.cumulative_risk", cumulative);
            _sessions.TryRemove(sessionId, out _);
            return EscalateAndBlock(sessionId, "Cumulative risk threshold exceeded");
        }

        var policyResults = _policy.EvaluateInput(input);
        if (policyResults.Any(r => r.Action == PolicyAction.Block))
            return GateVerdict.Block("Policy violation: " + policyResults.First().Message);

        if (!verdict.Allowed)
            return EscalateAndBlock(sessionId, verdict.BlockReason ?? "SafetyCoordinator block");

        return GateVerdict.Allow(verdict.RiskScore);
    }

    public async Task<GateVerdict> EvaluateOutputAsync(
        string output, string sessionId, CancellationToken ct = default)
    {
        var result = await _coordinator.EvaluateOutputAsync(output, ct);
        if (!result.Allowed)
            return GateVerdict.Block(result.BlockReason ?? "Output blocked");

        var issues = new List<string>();

        if (Regex.IsMatch(output, @"<script\b|javascript\s*:", RegexOptions.IgnoreCase))
            issues.Add("XSS pattern detected");
        if (Regex.IsMatch(output, @"DELETE\s+FROM|DROP\s+TABLE", RegexOptions.IgnoreCase))
            issues.Add("SQL injection pattern in output");
        if (Regex.IsMatch(output, @"(api_key|password|token|secret)\s*[:=]\s*['\""]\w{8,}"))
            issues.Add("Credential leak — redacted");

        return issues.Count > 0
            ? GateVerdict.BlockWithRedaction(string.Join("; ", issues))
            : GateVerdict.Allow(result.RiskScore);
    }

    public bool EvaluateToolCall(string toolName, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;

        var blockedPatterns = new[]
        {
            @"rm\s+-rf\s+/", @"\|\s*(bash|sh|pwsh)\b",
            @"(curl|wget)\s+\S+\s*\|\s*\w+", @"\b(Invoke-Expression|iex)\b",
            @"chmod\s+777\s+/", @":\(\)\s*\{\s*:\|:\s*&\s*\}\s*;"
        };

        return !blockedPatterns.Any(p =>
            Regex.IsMatch(input, p, RegexOptions.IgnoreCase));
    }

    public Dictionary<string, object> GetStats()
    {
        var now = DateTime.UtcNow;
        return new()
        {
            ["active_sessions"] = _sessions.Count,
            ["frozen_sessions"] = _sessions.Values.Count(s => s.FrozenUntil.HasValue && s.FrozenUntil > now)
        };
    }

    private GateVerdict EscalateAndBlock(string sessionId, string reason)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new SessionSafetyState());
        session.StrikeCount = Math.Min(session.StrikeCount + 1, CoolingDurations.Length - 1);

        var duration = CoolingDurations[session.StrikeCount];
        session.FrozenUntil = DateTime.UtcNow.Add(duration);

        _logger.LogWarning(
            "SafetyGate: Session {Session} strike={Strike}, frozen {Minutes}min. Reason: {Reason}",
            sessionId, session.StrikeCount, duration.TotalMinutes, reason);

        return session.StrikeCount == 1
            ? GateVerdict.Warn(reason + " (warning — further violations will freeze your session)")
            : GateVerdict.Block(reason + $" (session frozen {duration.TotalMinutes} min, strike {session.StrikeCount})");
    }

    private double ComputeInjectionScore(string text)
    {
        var lower = text.ToLowerInvariant();
        var hitCount = InjectionKeywords.Count(kw => lower.Contains(kw));
        return hitCount == 0 ? 0 : Math.Min(1.0, hitCount * _injectionScorePerHit);
    }

    private double UpdateCumulativeRisk(string sessionId, double currentRisk)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new SessionSafetyState());
        lock (session)
        {
            session.RiskHistory.Enqueue(currentRisk);
            if (session.RiskHistory.Count > 10)
                session.RiskHistory.Dequeue();

            if (session.RiskHistory.Count < 3) return 0.0;

            return session.RiskHistory.Average();
        }
    }

    private static string DecodeEncodings(string text)
    {
        var result = text;

        foreach (Match match in Base64Pattern.Matches(text).Cast<Match>())
        {
            try
            {
                var bytes = Convert.FromBase64String(match.Value);
                var decoded = Encoding.UTF8.GetString(bytes);
                if (decoded.All(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t'))
                    result = result.Replace(match.Value, decoded);
            }
            catch { }
        }

        result = Regex.Replace(result, @"\\u([0-9a-fA-F]{4})", m =>
        {
            try { return ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString(); }
            catch { return m.Value; }
        });

        return result;
    }
}
