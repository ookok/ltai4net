using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

public sealed class InstructionProvider : AIContextProvider
{
    private string? _cachedAgentsMd;
    private readonly string? _modelId;

    public InstructionProvider(string? modelId = null) : base(null, null, null)
    {
        _modelId = modelId;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var rules = BuildRules();

        var agentsMd = LoadAgentsMd();
        if (!string.IsNullOrEmpty(agentsMd))
            rules += $"\n\n[项目指令]\n{agentsMd}";

        var msg = new ChatMessage(ChatRole.System, rules);

        var msgs = context.AIContext?.Messages?.ToList() ?? [];
        msgs.Insert(0, msg);

        var instructions = context.AIContext?.Instructions ?? "";
        var modelGuidance = BuildModelGuidance();
        if (!string.IsNullOrEmpty(modelGuidance))
            instructions = string.IsNullOrEmpty(instructions)
                ? modelGuidance
                : instructions + "\n\n" + modelGuidance;

        return ValueTask.FromResult(new AIContext
        {
            Instructions = instructions,
            Messages = msgs,
            Tools = context.AIContext?.Tools,
        });
    }

    private string BuildModelGuidance()
    {
        if (string.IsNullOrEmpty(_modelId)) return "";

        if (_modelId.Contains("pro", StringComparison.OrdinalIgnoreCase) ||
            _modelId.Contains("deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
        {
            return "[模型提示]\n"
                 + "你运行在深度推理模式下。请注重逻辑推理、分步分析和深入思考。\n"
                 + "对于复杂问题，请在最终答案前展示完整的推理过程，确保每一步都是可验证的。";
        }

        if (_modelId.Contains("flash", StringComparison.OrdinalIgnoreCase) ||
            _modelId.Contains("fast", StringComparison.OrdinalIgnoreCase))
        {
            return "[模型提示]\n"
                 + "你运行在快速响应模式下。请注重效率、简洁和直接。\n"
                 + "优先提供最直接的答案，避免冗长的推理过程，在准确性和速度之间取得平衡。";
        }

        return "";
    }

    private static string BuildRules()
    {
        return "[操作规则]\n"
            + "1. 当工具返回「尚未获得权限」时，向用户展示路径并询问是否允许，用户同意后重新调用相同工具并设置 confirm=true。\n"
            + "2. 不要尝试其他工具代替未授权的操作。\n"
            + "3. 参数必须是正确的JSON类型（数字不要加引号，布尔值用true/false）。";
    }

    private string? LoadAgentsMd()
    {
        if (_cachedAgentsMd != null) return _cachedAgentsMd;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "AGENTS.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try { _cachedAgentsMd = File.ReadAllText(path); } catch { }
                break;
            }
        }

        return _cachedAgentsMd;
    }
}
