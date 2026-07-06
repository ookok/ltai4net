using System.Text.RegularExpressions;
using LTAI.Agent.Evolution;
using LTAI.Agent.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed record AntiPatternPatch(
    string Pattern,
    string Module,
    string Principle);

public sealed class AntiPatternPatchStep : IPipelineStep
{
    private readonly MetaSkillStore _skillStore;
    private readonly ILogger<AntiPatternPatchStep> _logger;

    public string Name => "AntiPatternPatch";

    private static readonly TimeSpan MinPatchInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastPatchTime = DateTime.MinValue;

    private static readonly (Regex Pattern, string Module, string Principle)[] DetectionRules =
    [
        (new Regex(@"I don'?t have (enough |the required |access to )?", RegexOptions.IgnoreCase),
            "AgentEngineering", "Before delegating a sub-task, verify the agent has the required tools and permissions"),

        (new Regex(@"I couldn'?t find (any |matching |relevant )?", RegexOptions.IgnoreCase),
            "TaskDecomposition", "When a sub-task fails to find information, merge it with the preceding sub-task to broaden context"),

        (new Regex(@"(error|exception|failed) (while |during |trying to )?(call|execute|run|invoke)",
            RegexOptions.IgnoreCase),
            "WorkflowOrchestration", "Add fallback recovery sub-tasks after any tool that may fail"),

        (new Regex(@"The (query|question|task|request).*(unclear|ambiguous|vague|not specific)",
            RegexOptions.IgnoreCase),
            "TaskDecomposition", "When the query is ambiguous, produce multiple parallel decomposition paths and select the best one"),

        (new Regex(@"(timeout|timed out|too long|exceeded)", RegexOptions.IgnoreCase),
            "WorkflowOrchestration", "Split long-running sub-tasks into smaller sequential steps with timeout monitoring"),
    ];

    public AntiPatternPatchStep(
        MetaSkillStore skillStore,
        ILogger<AntiPatternPatchStep>? logger = null)
    {
        _skillStore = skillStore;
        _logger = logger ?? NullLogger<AntiPatternPatchStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var assistantMsg = context.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (assistantMsg?.Text == null)
            return context;

        var text = assistantMsg.Text;
        var detectedPatches = new List<AntiPatternPatch>();

        foreach (var (pattern, module, principle) in DetectionRules)
        {
            if (pattern.IsMatch(text))
                detectedPatches.Add(new AntiPatternPatch(pattern.ToString(), module, principle));
        }

        if (detectedPatches.Count == 0)
            return context;

        var now = DateTime.UtcNow;
        if (now - _lastPatchTime < MinPatchInterval)
        {
            _logger.LogDebug("AntiPatternPatchStep: skipped {Count} patch(es), min interval not elapsed",
                detectedPatches.Count);
            return context;
        }

        var current = await _skillStore.GetLatestAsync(context.CancellationToken).ConfigureAwait(false);

        foreach (var dp in detectedPatches)
        {
            var patch = new MetaSkillPatch(dp.Module, dp.Principle, 0.6);
            try
            {
                await _skillStore.ApplyPatchAsync([patch], context.CancellationToken)
                    .ConfigureAwait(false);
                _lastPatchTime = now;
                _logger.LogWarning("AntiPatternPatchStep: hot-patched Meta-Skill v{V} → [{Mod}] {Princ}",
                    current.Version + 1, dp.Module, Truncate(dp.Principle, 80));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AntiPatternPatchStep: failed to apply hot-patch");
            }
        }

        var patchMsg = "## [P:hot-patch] — 在线自纠正\n" +
            "检测到以下反模式，已热补丁 Meta-Skill：\n" +
            string.Join("\n", detectedPatches.Select(dp =>
                $"  - [{dp.Module}] {dp.Principle}"));

        lock (context.MessagesLock)
            context.Messages.Add(new ChatMessage(ChatRole.System, patchMsg));

        return context;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
