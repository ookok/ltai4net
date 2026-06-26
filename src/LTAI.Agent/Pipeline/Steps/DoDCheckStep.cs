using System.Text.Json;
using LTAI.Agent.Tools.Review;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class DoDCheckStep : IPipelineStep
{
    private readonly ILogger<DoDCheckStep> _logger;
    private readonly ReviewRuleEngine? _ruleEngine;

    public string Name => "DoDCheck";

    public DoDCheckStep(ILogger<DoDCheckStep>? logger = null, ReviewRuleEngine? ruleEngine = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DoDCheckStep>.Instance;
        _ruleEngine = ruleEngine;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (!context.TryGet<DoDConfig>("DoD", out var dod) || dod == null)
            return context;

        var failedCriteria = new List<string>();

        foreach (var criterion in dod.Criteria)
        {
            var passed = criterion switch
            {
                "no_syntax_errors" => await CheckNoSyntaxErrorsAsync(context).ConfigureAwait(false),
                "has_tests" => CheckHasTests(context),
                "no_todos" => CheckNoTodos(context),
                "documentation_updated" => CheckDocumentationUpdated(context),
                "no_placeholders" => CheckNoPlaceholders(context),
                "lint_clean" => await CheckLintCleanAsync(context).ConfigureAwait(false),
                _ => true
            };
            if (!passed)
                failedCriteria.Add(criterion);
        }

        if (failedCriteria.Count > 0)
        {
            context.DoDBlocked = true;
            context.Set("DoDFailedCriteria", failedCriteria);

            var msg = $"⚠️ Definition of Done 未通过: {string.Join(", ", failedCriteria)}";
            lock (context.MessagesLock) context.Messages.Add(new ChatMessage(ChatRole.System, msg));

            _logger.LogWarning("DoD failed: {Criteria}", string.Join(", ", failedCriteria));
        }
        else
        {
            context.DoDBlocked = false;
            _logger.LogInformation("DoD passed all {Count} criteria", dod.Criteria.Count);
        }

        return context;
    }

    private Task<bool> CheckNoSyntaxErrorsAsync(MessageContext context)
    {
        // Read via ConcurrentDictionary to avoid race with GrammarCheckStep in parallel group.
        // GrammarCheckStep writes context.Set("GrammarErrors", List<GrammarError>) which is thread-safe.
        var hasErrors = context.TryGet<object>("GrammarErrors", out _);
        return Task.FromResult(!hasErrors);
    }

    private static bool CheckHasTests(MessageContext context)
    {
        foreach (var (name, _, result) in context.ToolCalls)
        {
            if (name.Contains("write", StringComparison.OrdinalIgnoreCase) &&
                result.Contains("test", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool CheckNoTodos(MessageContext context)
    {
        foreach (var (_, _, result) in context.ToolCalls)
        {
            if (result.Contains("TODO", StringComparison.Ordinal) ||
                result.Contains("FIXME", StringComparison.Ordinal))
                return false;
        }
        foreach (var msg in context.Messages)
        {
            if (!string.IsNullOrEmpty(msg.Text) &&
                (msg.Text.Contains("TODO") || msg.Text.Contains("FIXME")))
                return false;
        }
        return true;
    }

    private static bool CheckDocumentationUpdated(MessageContext context)
    {
        foreach (var (name, _, _) in context.ToolCalls)
        {
            if (name.Contains("doc", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("readme", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool CheckNoPlaceholders(MessageContext context)
    {
        foreach (var msg in context.Messages)
        {
            if (!string.IsNullOrEmpty(msg.Text))
            {
                if (msg.Text.Contains("{{") || msg.Text.Contains("}}"))
                    return false;
            }
        }
        return true;
    }

    private Task<bool> CheckLintCleanAsync(MessageContext context)
    {
        try
        {
            foreach (var (name, _, result) in context.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(result)) continue;

                // Only check files that were written/edited
                if (!name.Contains("write", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("edit", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("replace", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Basic lint checks: detect common issues in generated code
                if (result.Contains("<<<<<<< ") || result.Contains("=======") ||
                    result.Contains(">>>>>>> "))
                    return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DoDCheck: lint evaluation failed");
        }

        return Task.FromResult(true);
    }
}

public sealed record DoDConfig
{
    public IReadOnlyList<string> Criteria { get; init; } = [];
    public string? Prompt { get; init; }

    public static DoDConfig FromYaml(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new DoDConfig();
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<string>>(trimmed);
                return new DoDConfig { Criteria = items ?? [] };
            }
            catch { /* JSON parse failed — fall back to comma-delimited */ }
        }
        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new DoDConfig { Criteria = parts };
    }

    public static DoDConfig DefaultCode => new()
    {
        Criteria = ["no_syntax_errors", "no_todos", "no_placeholders"],
        Prompt = "代码必须无语法错误、无 TODO/FIXME 占位符、无 {{}} 模板残留。"
    };

    public static DoDConfig DefaultTest => new()
    {
        Criteria = ["no_syntax_errors", "lint_clean"],
        Prompt = "测试代码必须通过语法检查和 lint 检查。"
    };
}
