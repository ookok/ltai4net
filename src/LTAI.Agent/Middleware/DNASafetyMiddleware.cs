using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class DNASafetyMiddleware
{
    private readonly ILogger<DNASafetyMiddleware> _logger;

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

    public DNASafetyMiddleware(ILogger<DNASafetyMiddleware> logger)
    {
        _logger = logger;
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

        if (userMsg?.Text is not null && IsBlocked(userMsg.Text))
        {
            _logger.LogWarning("DNASafetyMiddleware: Blocked unsafe input");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                "[Safety] Your request was blocked by the content safety filter. Please rephrase your request in a safer manner."));
        }

        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        if (response.Text is not null && IsBlocked(response.Text))
        {
            _logger.LogWarning("DNASafetyMiddleware: Blocked unsafe output");
            response.Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, "[Safety] The response was filtered for content safety compliance.")
            };
        }

        return response;
    }

    private static bool IsBlocked(string text)
    {
        var lower = text.ToLowerInvariant();
        return BlockedPatterns.Any(p => lower.Contains(p));
    }
}
