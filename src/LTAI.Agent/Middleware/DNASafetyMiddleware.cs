using LTAI.DNA.Safety;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class DNASafetyMiddleware
{
    private readonly ILogger<DNASafetyMiddleware> _logger;
    private readonly SafetyCoordinator? _safety;

    private static readonly HashSet<string> BlockedPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "hack", "exploit", "malware", "ransomware", "phishing",
        "social engineering", "password crack", "ddos", "backdoor",
        "illegal", "harmful", "self-harm", "violence",
        "黑客", "越权", "提权", "脱库", "漏洞利用", "0day", "payload",
        "木马", "蠕虫", "病毒植入", "钓鱼网站", "撞库", "爆破密码",
        "自杀", "自残", "暴力恐怖", "制造武器", "毒品制作",
        "人肉搜索", "窃取隐私", "非法入侵", "伪造证件",
        "色情", "赌博", "洗钱"
    };

    public DNASafetyMiddleware(ILogger<DNASafetyMiddleware> logger, SafetyCoordinator? safety = null)
    {
        _logger = logger;
        _safety = safety;
    }

    public async Task<AgentResponse> InvokeAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is not null)
        {
            if (IsBlocked(userMsg.Text))
            {
                _logger.LogWarning("DNASafetyMiddleware: Blocked unsafe input (keyword)");
                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                    "[Safety] Your request was blocked by the content safety filter."));
            }

            if (_safety != null)
            {
                var verdict = await _safety.EvaluateAsync(userMsg.Text, null, cancellationToken);
                if (!verdict.Allowed)
                {
                    _logger.LogWarning("DNASafetyMiddleware: Blocked by SafetyCoordinator, risk={Risk:F2}", verdict.RiskScore);
                    _safety.ReportIncident($"Blocked input: {string.Join(", ", verdict.Threats)}");
                    return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                        $"[Safety] Request blocked: {verdict.BlockReason}"));
                }
            }
        }

        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        if (response.Text is not null)
        {
            if (IsBlocked(response.Text))
            {
                _logger.LogWarning("DNASafetyMiddleware: Blocked unsafe output (keyword)");
                response.Messages = new List<ChatMessage>
                {
                    new(ChatRole.Assistant, "[Safety] The response was filtered for content safety compliance.")
                };
                return response;
            }

            if (_safety != null)
            {
                var outputVerdict = await _safety.EvaluateOutputAsync(response.Text, cancellationToken);
                if (!outputVerdict.Allowed)
                {
                    _logger.LogWarning("DNASafetyMiddleware: Output blocked by SafetyCoordinator, risk={Risk:F2}", outputVerdict.RiskScore);
                    response.Messages = new List<ChatMessage>
                    {
                        new(ChatRole.Assistant, "[Safety] Output filtered for safety compliance.")
                    };
                }
            }
        }

        return response;
    }

    private static bool IsBlocked(string text)
    {
        var lower = text.ToLowerInvariant();
        return BlockedPatterns.Any(p => lower.Contains(p));
    }
}
