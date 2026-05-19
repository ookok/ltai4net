using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Life;

public enum NodeStatus
{
    Success,
    Failure,
    Running
}

public record TreeContext(
    string UserInput,
    Dictionary<string, string> Metadata,
    List<string> History,
    List<string> Errors,
    List<string> Results,
    int Depth,
    int MaxDepth = 10);

public abstract class BTNode
{
    public string Name { get; }
    public List<BTNode> Children { get; } = new();

    protected BTNode(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public abstract NodeStatus Tick(TreeContext ctx);

    public void AddChild(BTNode node)
    {
        Children.Add(node);
    }
}

public sealed class Sequence : BTNode
{
    private readonly ILogger<Sequence>? _logger;

    public Sequence(string name, ILogger<Sequence>? logger = null) : base(name)
    {
        _logger = logger;
    }

    public override NodeStatus Tick(TreeContext ctx)
    {
        if (ctx.Depth >= ctx.MaxDepth)
        {
            ctx.Errors.Add($"MaxDepth ({ctx.MaxDepth}) exceeded at node '{Name}'");
            _logger?.LogWarning("MaxDepth exceeded at node '{Name}'", Name);
            return NodeStatus.Failure;
        }

        var childCtx = ctx with { Depth = ctx.Depth + 1 };

        foreach (var child in Children)
        {
            var status = child.Tick(childCtx);
            ctx.History.Add($"[Sequence:{Name}] child '{child.Name}' → {status}");

            if (status != NodeStatus.Success)
            {
                _logger?.LogDebug("Sequence '{Name}' failed at child '{ChildName}'", Name, child.Name);
                return status;
            }
        }

        _logger?.LogDebug("Sequence '{Name}' succeeded", Name);
        return NodeStatus.Success;
    }
}

public sealed class Selector : BTNode
{
    private readonly ILogger<Selector>? _logger;

    public Selector(string name, ILogger<Selector>? logger = null) : base(name)
    {
        _logger = logger;
    }

    public override NodeStatus Tick(TreeContext ctx)
    {
        var childCtx = ctx with { Depth = ctx.Depth + 1 };

        foreach (var child in Children)
        {
            var status = child.Tick(childCtx);
            ctx.History.Add($"[Selector:{Name}] child '{child.Name}' → {status}");

            if (status == NodeStatus.Success)
            {
                _logger?.LogDebug("Selector '{Name}' succeeded via child '{ChildName}'", Name, child.Name);
                return NodeStatus.Success;
            }
        }

        _logger?.LogDebug("Selector '{Name}' failed - all children failed", Name);
        return NodeStatus.Failure;
    }
}

public sealed class ActionNode : BTNode
{
    public Func<TreeContext, NodeStatus> Fn { get; }

    public ActionNode(string name, Func<TreeContext, NodeStatus> fn) : base(name)
    {
        Fn = fn ?? throw new ArgumentNullException(nameof(fn));
    }

    public override NodeStatus Tick(TreeContext ctx)
    {
        var status = Fn(ctx);
        ctx.History.Add($"[Action:{Name}] → {status}");
        return status;
    }
}

public sealed class ConditionNode : BTNode
{
    public Func<TreeContext, bool> Pred { get; }

    public ConditionNode(string name, Func<TreeContext, bool> pred) : base(name)
    {
        Pred = pred ?? throw new ArgumentNullException(nameof(pred));
    }

    public override NodeStatus Tick(TreeContext ctx)
    {
        var result = Pred(ctx);
        var status = result ? NodeStatus.Success : NodeStatus.Failure;
        ctx.History.Add($"[Condition:{Name}] → {status}");
        return status;
    }
}

public sealed class ModelDecisionNode : BTNode
{
    public Func<string, string>? LlmCall { get; set; }
    private readonly ILogger<ModelDecisionNode>? _logger;

    public ModelDecisionNode(string name, Func<string, string>? llmCall = null, ILogger<ModelDecisionNode>? logger = null)
        : base(name)
    {
        LlmCall = llmCall;
        _logger = logger;
    }

    public override NodeStatus Tick(TreeContext ctx)
    {
        if (Children.Count == 0)
        {
            ctx.Errors.Add($"ModelDecisionNode '{Name}' has no children");
            return NodeStatus.Failure;
        }

        var options = new List<string>();
        for (int i = 0; i < Children.Count; i++)
            options.Add($"{i + 1}. {Children[i].Name}");

        var prompt = $@"Given the user input: '{ctx.UserInput}'
Select the most appropriate action from the options below. Respond with only the option number.

{string.Join("\n", options)}

Your choice (number only):";

        string response;
        if (LlmCall != null)
        {
            response = LlmCall(prompt);
            _logger?.LogDebug("LLM response for '{Name}': {Response}", Name, response);
        }
        else
        {
            response = "1";
            _logger?.LogDebug("No LLM call configured for '{Name}', defaulting to first child", Name);
        }

        var match = Regex.Match(response, @"\b[1-9]\b");
        if (!match.Success || !int.TryParse(match.Value, out var choice) ||
            choice < 1 || choice > Children.Count)
        {
            ctx.Errors.Add($"ModelDecisionNode '{Name}': invalid LLM response '{response}'");
            _logger?.LogWarning("Invalid LLM response for '{Name}': {Response}", Name, response);
            return NodeStatus.Failure;
        }

        var selected = Children[choice - 1];
        ctx.History.Add($"[ModelDecision:{Name}] routing to '{selected.Name}' (choice={choice})");
        ctx.Results.Add($"Selected: {selected.Name}");

        var childCtx = ctx with { Depth = ctx.Depth + 1 };
        return selected.Tick(childCtx);
    }
}

public static class BehaviorTreeFactory
{
    public static BTNode BuildAgenticTree(
        List<string> taskSteps,
        List<string> fallbackSteps,
        List<string> preChecks,
        ILogger? logger = null)
    {
        var root = new Selector("AgentRoot", logger as ILogger<Selector>);

        var primarySequence = new Sequence("PrimaryPlan", logger as ILogger<Sequence>);

        foreach (var check in preChecks)
        {
            var checkNode = new ActionNode($"PreCheck_{check}", ctx =>
            {
                ctx.History.Add($"Running pre-check: {check}");
                ctx.Results.Add($"PreCheck passed: {check}");
                return NodeStatus.Success;
            });
            primarySequence.AddChild(checkNode);
        }

        if (taskSteps.Count > 0 && taskSteps.TrueForAll(s => s.StartsWith("parallel:", StringComparison.OrdinalIgnoreCase)))
        {
            var parallel = new Selector("ParallelTasks", logger as ILogger<Selector>);
            foreach (var step in taskSteps)
            {
                var clean = step["parallel:".Length..].Trim();
                parallel.AddChild(new ActionNode(clean, ctx =>
                {
                    ctx.History.Add($"Parallel task: {clean}");
                    ctx.Results.Add($"Completed: {clean}");
                    return NodeStatus.Success;
                }));
            }
            primarySequence.AddChild(parallel);
        }
        else
        {
            foreach (var step in taskSteps)
            {
                primarySequence.AddChild(new ActionNode(step, ctx =>
                {
                    ctx.History.Add($"Task: {step}");
                    ctx.Results.Add($"Completed: {step}");
                    return NodeStatus.Success;
                }));
            }
        }

        root.AddChild(primarySequence);

        if (fallbackSteps.Count > 0)
        {
            var fallbackSequence = new Sequence("FallbackPlan", logger as ILogger<Sequence>);
            foreach (var step in fallbackSteps)
            {
                fallbackSequence.AddChild(new ActionNode(step, ctx =>
                {
                    ctx.History.Add($"Fallback: {step}");
                    ctx.Results.Add($"Fallback completed: {step}");
                    return NodeStatus.Success;
                }));
            }
            root.AddChild(fallbackSequence);
        }

        return root;
    }

    public static BTNode LinearPlanToTree(List<string> steps, string? fallbackHint = null)
    {
        var sequence = new Sequence("LinearPlan");

        foreach (var step in steps)
        {
            sequence.AddChild(new ActionNode(step, ctx =>
            {
                ctx.History.Add($"Executing: {step}");
                ctx.Results.Add($"Done: {step}");
                return NodeStatus.Success;
            }));
        }

        if (!string.IsNullOrWhiteSpace(fallbackHint))
        {
            sequence.AddChild(new ActionNode($"Fallback: {fallbackHint}", ctx =>
            {
                ctx.History.Add($"Fallback triggered: {fallbackHint}");
                ctx.Errors.Add(fallbackHint);
                return NodeStatus.Failure;
            }));
        }

        return sequence;
    }
}
