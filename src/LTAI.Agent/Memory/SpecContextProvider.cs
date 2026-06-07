using LTAI.Core.Specs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

/// <summary>
/// Injects the active spec (spec + plan + tasks) into agent context when
/// the spec status is >= <see cref="SpecStatus.Planned"/>.
/// Layer position: right after L1Essential, before compaction.
/// </summary>
public sealed class SpecContextProvider : AIContextProvider
{
    private const int MaxSpecTokens = 800;
    private readonly SpecService _specs;
    private readonly ILogger<SpecContextProvider>? _logger;

    public SpecContextProvider(
        SpecService specs,
        ILogger<SpecContextProvider>? logger = null)
    {
        _specs = specs ?? throw new ArgumentNullException(nameof(specs));
        _logger = logger;
    }

    public override IReadOnlyList<string> StateKeys => ["SpecContext"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        try
        {
            var active = FindActiveSpec();
            if (active == null)
                return ValueTask.FromResult(new AIContext());

            var specMd = _specs.ReadSpec(active.Name) ?? "";
            var planMd = _specs.ReadPlan(active.Name);
            var tasksMd = _specs.ReadTasks(active.Name);

            var lines = new List<string>
            {
                $"## Active Spec: {active.Name}",
                $"> Status: {active.Status}  |  Created: {active.CreatedAt:yyyy-MM-dd}",
            };
            if (!string.IsNullOrEmpty(active.Description))
                lines.Add($"> {active.Description}");

            lines.Add("");
            var budget = MaxSpecTokens * 4;

            // spec.md — extract first N chars
            if (!string.IsNullOrEmpty(specMd))
            {
                var specSnippet = specMd.Replace('\n', ' ').Trim();
                if (specSnippet.Length > budget) specSnippet = specSnippet[..(budget - 50)] + "...";
                lines.Add("### Spec");
                lines.Add(specSnippet);
                budget -= specSnippet.Length;
            }

            // plan.md
            if (!string.IsNullOrEmpty(planMd) && budget > 100)
            {
                var planSnippet = planMd.Replace('\n', ' ').Trim();
                if (planSnippet.Length > budget) planSnippet = planSnippet[..(budget - 50)] + "...";
                lines.Add("");
                lines.Add("### Plan");
                lines.Add(planSnippet);
                budget -= planSnippet.Length;
            }

            // tasks.md
            if (!string.IsNullOrEmpty(tasksMd) && budget > 100)
            {
                var tasksSnippet = tasksMd.Replace('\n', ' ').Trim();
                if (tasksSnippet.Length > budget) tasksSnippet = tasksSnippet[..(budget - 50)] + "...";
                lines.Add("");
                lines.Add("### Tasks");
                lines.Add(tasksSnippet);
            }

            _logger?.LogDebug("SpecContextProvider: injected '{Spec}' ({Status})", active.Name, active.Status);
            return ValueTask.FromResult(new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, string.Join("\n", lines))],
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SpecContextProvider: failed");
            return ValueTask.FromResult(new AIContext());
        }
    }

    private SpecManifest? FindActiveSpec()
    {
        var all = _specs.List();
        // Find the highest-status spec that has content
        return all
            .Where(m => m.Status >= SpecStatus.Planned && _specs.ReadSpec(m.Name) != null)
            .MaxBy(m => (int)m.Status);
    }
}
