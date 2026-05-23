using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.AI.Governors;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class PromptShieldMiddleware
{
    private readonly ILogger<PromptShieldMiddleware> _logger;
    private readonly ConfidenceCalibrator _calibrator;
    private const double BlockThreshold = 0.35;
    private const double WarnThreshold = 0.50;
    private const double CumulativeRiskThreshold = 0.6;
    private const int CumulativeWindow = 10;
    private static readonly ConcurrentDictionary<string, Queue<(string text, double risk)>> _sessionBuffers = new();

    private static readonly Regex Base64Pattern = new(
        @"[A-Za-z0-9+/]{20,}={0,2}",
        RegexOptions.Compiled);

    private static readonly string[] InjectionKeywords =
    [
        "ignore all previous", "ignore previous instructions", "ignore all instructions",
        "disregard all previous", "forget all previous", "override system prompt",
        "you are now", "act as if", "pretend you are", "new instructions",
        "system prompt", "reveal your instructions", "output your prompt",
        "忽略之前", "忽略所有指令", "忽略以上", "覆盖系统", "无视规则",
        "你现在是", "扮演一个", "假设你是", "输出你的提示词", "显示系统提示"
    ];

    public PromptShieldMiddleware(ILogger<PromptShieldMiddleware> logger)
    {
        _logger = logger;
        _calibrator = new ConfidenceCalibrator();
    }

    public Task<AgentResponse> InvokeAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var msgList = messages.ToList();
        var filtered = new List<ChatMessage>(msgList.Count);
        var sessionId = (session as LTAIAgentSession)?.SessionId ?? session?.GetHashCode().ToString() ?? "anon";

        foreach (var msg in msgList)
        {
            if (msg.Role == ChatRole.User && msg.Text is not null)
            {
                var sanitized = SanitizeInput(msg.Text);
                var decoded = DecodeEncodings(sanitized);

                if (decoded != sanitized)
                {
                    _logger.LogWarning("PromptShield: Encoded content detected and decoded for session {Session}", sessionId);
                    var decodedRisk = ComputeInjectionRisk(decoded);
                    if (decodedRisk > 0.5)
                    {
                        _logger.LogWarning("PromptShield: BLOCKED encoded injection, risk={Risk:F2}", decodedRisk);
                        return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant,
                            "[PromptShield] Encoded injection detected. Input blocked.")));
                    }
                }

                filtered.Add(new ChatMessage(msg.Role, sanitized));
            }
            else
            {
                filtered.Add(msg);
            }
        }

        var toolName = session?.GetType().Name ?? options?.GetType().Name ?? "agent";
        var gate = _calibrator.Calibrate(toolName, 0.8, 0.85);
        if (gate.CalibratedConfidence < BlockThreshold)
        {
            _logger.LogWarning("PromptShield: BLOCKED input, confidence {Conf:F2} below threshold {Threshold} for {Tool}",
                gate.CalibratedConfidence, BlockThreshold, toolName);
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant,
                "[PromptShield] Input blocked: safety threshold exceeded. Please rephrase your request.")));
        }

        var userText = msgList.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var currentRisk = ComputeInjectionRisk(userText);
        var cumulativeRisk = UpdateCumulativeRisk(sessionId, userText, currentRisk);

        if (cumulativeRisk > CumulativeRiskThreshold)
        {
            _logger.LogWarning("PromptShield: BLOCKED cumulative injection, risk={Risk:F2} for session {Session}",
                cumulativeRisk, sessionId);
            _sessionBuffers.TryRemove(sessionId, out _);
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant,
                "[PromptShield] Cumulative risk detected. Input blocked.")));
        }

        if (gate.CalibratedConfidence < WarnThreshold)
            _logger.LogWarning("PromptShield: low confidence {Conf:F2} for {Tool}", gate.CalibratedConfidence, toolName);

        _logger.LogDebug("PromptShieldMiddleware: Input sanitized, cumulative risk={Risk:F2}", cumulativeRisk);
        return innerAgent.RunAsync(filtered, session, options, cancellationToken);
    }

    private static double ComputeInjectionRisk(string text)
    {
        var lower = text.ToLowerInvariant();
        var hitCount = InjectionKeywords.Count(kw => lower.Contains(kw));
        if (hitCount == 0) return 0.0;
        return Math.Min(1.0, hitCount * 0.35);
    }

    private static double UpdateCumulativeRisk(string sessionId, string text, double currentRisk)
    {
        var queue = _sessionBuffers.GetOrAdd(sessionId, _ => new Queue<(string, double)>());
        lock (queue)
        {
            queue.Enqueue((text, currentRisk));
            while (queue.Count > CumulativeWindow)
                queue.Dequeue();

            if (queue.Count < 3) return 0.0;

            var totalRisk = 0.0;
            var keywordHits = 0;
            foreach (var (t, r) in queue)
            {
                totalRisk += r;
                var lower = t.ToLowerInvariant();
                keywordHits += InjectionKeywords.Count(kw => lower.Contains(kw));
            }

            var spreadScore = Math.Min(1.0, keywordHits * 0.2);
            var avgRisk = totalRisk / queue.Count;
            return Math.Max(avgRisk, spreadScore);
        }
    }

    private static string DecodeEncodings(string text)
    {
        var result = text;

        foreach (Match match in Base64Pattern.Matches(text))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(match.Value));
                if (decoded.All(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t'))
                    result = result.Replace(match.Value, decoded);
            }
            catch { }
        }

        result = DecodeRot13(result);

        result = Regex.Replace(result, @"\\u([0-9a-fA-F]{4})", m =>
        {
            try { return ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString(); }
            catch { return m.Value; }
        });

        return result;
    }

    private static string DecodeRot13(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c >= 'a' && c <= 'z')
                chars[i] = (char)((c - 'a' + 13) % 26 + 'a');
            else if (c >= 'A' && c <= 'Z')
                chars[i] = (char)((c - 'A' + 13) % 26 + 'A');
        }
        return new string(chars);
    }

    private static string SanitizeInput(string text)
    {
        return text.Replace("<|im_start|>", "<im_start>")
                   .Replace("<|im_end|>", "<im_end>")
                   .Replace("<|endoftext|>", "<endoftext>");
    }
}
