using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class AbstentionCheckStep : IPipelineStep
{
    private readonly ILogger<AbstentionCheckStep> _logger;

    public string Name => "AbstentionCheck";

    public AbstentionCheckStep(ILogger<AbstentionCheckStep>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AbstentionCheckStep>.Instance;
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var rules = EvaluateStoppingRules(context);

        if (rules.Count > 0)
        {
            context.AbstentionBlocked = true;
            context.Set("AbstentionRules", rules);

            var msg = "⏹ Agentic Abstention: " + string.Join("; ", rules);
            lock (context.MessagesLock)
                context.Messages.Add(new ChatMessage(ChatRole.System, msg));

            _logger.LogWarning("AbstentionCheck: blocked ({Rules})", string.Join(", ", rules));
        }
        else
        {
            context.AbstentionBlocked = false;
        }

        return Task.FromResult(context);
    }

    private static List<string> EvaluateStoppingRules(MessageContext context)
    {
        var rules = new List<string>();
        var calls = context.ToolCalls;

        if (calls.Count == 0)
            return rules;

        var lastFew = calls.TakeLast(3).ToList();

        if (lastFew.Count >= 2)
        {
            bool allSame = true;
            for (int i = 1; i < lastFew.Count; i++)
            {
                if (lastFew[i].Name != lastFew[0].Name || lastFew[i].Arguments != lastFew[0].Arguments)
                {
                    allSame = false;
                    break;
                }
            }
            if (allSame)
                rules.Add("重复工具调用: 连续 " + lastFew.Count + " 次相同工具/参数");
        }

        var recentCalls = calls.TakeLast(4).ToList();
        if (recentCalls.Count >= 3 && recentCalls.All(c => string.IsNullOrWhiteSpace(c.Result)))
            rules.Add("连续工具调用返回空结果 (最近 " + recentCalls.Count + " 次)");

        if (recentCalls.Count >= 2 && recentCalls.All(c =>
                c.Result.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                c.Result.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                c.Result.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            rules.Add("连续工具调用返回错误");

        var readFiles = calls.Where(c => c.Name.Contains("Read", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Arguments).ToList();
        if (readFiles.Count >= 4 && readFiles.Distinct().Count() <= 1)
            rules.Add("重复读取同一文件但未产生修改 (已读 " + readFiles.Count + " 次)");

        if (calls.Count >= 4)
        {
            var groups = calls.GroupBy(c => c.Name);
            var dominant = groups.OrderByDescending(g => g.Count()).First();
            if (dominant.Count() >= calls.Count * 0.7 && groups.Count() <= 2)
                rules.Add("工具调用类型单一: " + dominant.Key + " 占 " + dominant.Count() + "/" + calls.Count);
        }

        if (!string.IsNullOrEmpty(context.PipelineError))
            rules.Add("管线错误: " + context.PipelineError);

        return rules;
    }
}
