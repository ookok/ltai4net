// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  StepChainBuilder — maps PipelineConfig / DecisionTreeConfig
//  to the new WorkflowStep tree, and serializes WorkflowStep → JSON.
//
//  Phase 2b: bridges existing YAML/JSON hot-editable configs
//  (PipelineConfig, DecisionTreeConfig) with the new IExecutionEngine
//  step types (HandoffStep, SequentialStep, ConcurrentStep, etc.).
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using LTAI.Agent.Workflows;

namespace LTAI.Agent.Execution;

/// <summary>
/// Builds a <see cref="WorkflowStep"/> tree from existing YAML/JSON configs
/// (PipelineConfig / DecisionTreeConfig), and serializes WorkflowStep
/// trees back to JSON for DevUI display and testing.
///
/// This bridges the old hot-editable config format with the new
/// IExecutionEngine step types — no existing files need to change.
/// </summary>
public static class StepChainBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Build a WorkflowStep tree from a PipelineConfig.
    /// Supports nested steps (handoff, sequential, concurrent).
    /// </summary>
    public static WorkflowStep? FromPipelineConfig(PipelineConfig config)
    {
        if (config == null) return null;

        // If the config has typed steps, build the tree
        if (config.Steps.Count > 0)
        {
            return BuildSteps(config.Steps);
        }

        // Fallback: flat agent list → sequential or concurrent
        if (config.Agents.Count == 0) return null;

        var handoffSteps = config.Agents
            .Select(name => (WorkflowStep)new HandoffStep(name) { Name = $"handoff:{name}" })
            .ToList();

        return config.Type.ToLowerInvariant() switch
        {
            "sequential" => new SequentialStep(handoffSteps) { Name = "pipeline" },
            "concurrent" => new ConcurrentStep(handoffSteps) { Name = "pipeline" },
            _ => handoffSteps.Count == 1 ? handoffSteps[0] : new SequentialStep(handoffSteps) { Name = "pipeline" },
        };
    }

    /// <summary>
    /// Build a WorkflowStep tree from a list of PipelineSteps.
    /// Handles nesting for composite workflows.
    /// </summary>
    private static WorkflowStep? BuildSteps(IReadOnlyList<PipelineStep> steps)
    {
        if (steps == null || steps.Count == 0) return null;

        // Single step
        if (steps.Count == 1)
        {
            return BuildSingleStep(steps[0]);
        }

        // Multiple steps — wrap in SequentialStep
        var built = new List<WorkflowStep>(steps.Count);
        foreach (var step in steps)
        {
            var builtStep = BuildSingleStep(step);
            if (builtStep != null)
                built.Add(builtStep);
        }
        return built.Count switch
        {
            0 => null,
            1 => built[0],
            _ => new SequentialStep(built) { Name = "composite" },
        };
    }

    private static WorkflowStep? BuildSingleStep(PipelineStep step)
    {
        return step.Type.ToLowerInvariant() switch
        {
            "handoff" => BuildHandoffStep(step),
            "sequential" => BuildSequentialStep(step),
            "concurrent" => BuildConcurrentStep(step),
            "conditional" => BuildConditionalStep(step),
            "retry" => BuildRetryStep(step),
            "noop" => new NoopStep(step.Name) { Name = step.Name },
            _ => BuildHandoffStep(step), // default: handoff
        };
    }

    private static HandoffStep BuildHandoffStep(PipelineStep step)
    {
        var agentName = step.Agents.Count > 0 ? step.Agents[0] : step.Name;
        return new HandoffStep(agentName) { Name = step.Name };
    }

    private static SequentialStep BuildSequentialStep(PipelineStep step)
    {
        var subSteps = new List<WorkflowStep>();
        if (step.Steps.Count > 0)
        {
            foreach (var sub in step.Steps)
            {
                var built = BuildSingleStep(sub);
                if (built != null) subSteps.Add(built);
            }
        }
        else
        {
            // Flat agent list → create HandoffSteps
            subSteps.AddRange(step.Agents
                .Select(name => (WorkflowStep)new HandoffStep(name) { Name = $"handoff:{name}" }));
        }
        return new SequentialStep(subSteps) { Name = step.Name };
    }

    private static ConcurrentStep BuildConcurrentStep(PipelineStep step)
    {
        var subSteps = new List<WorkflowStep>();
        if (step.Steps.Count > 0)
        {
            foreach (var sub in step.Steps)
            {
                var built = BuildSingleStep(sub);
                if (built != null) subSteps.Add(built);
            }
        }
        else
        {
            subSteps.AddRange(step.Agents
                .Select(name => (WorkflowStep)new HandoffStep(name) { Name = $"handoff:{name}" }));
        }
        return new ConcurrentStep(subSteps) { Name = step.Name };
    }

    private static ConditionalStep BuildConditionalStep(PipelineStep step)
    {
        var trueStep = step.Steps.Count > 0
            ? (BuildSingleStep(step.Steps[0]) ?? new NoopStep("true") { Name = "true" })
            : (WorkflowStep)new NoopStep("true") { Name = "true" };

        var falseStep = step.Steps.Count > 1
            ? (BuildSingleStep(step.Steps[1]) ?? new NoopStep("false") { Name = "false" })
            : (WorkflowStep)new NoopStep("false") { Name = "false" };

        return new ConditionalStep(step.Name, trueStep, falseStep) { Name = step.Name };
    }

    private static RetryStep BuildRetryStep(PipelineStep step)
    {
        var inner = step.Steps.Count > 0
            ? (BuildSingleStep(step.Steps[0]) ?? new NoopStep("inner") { Name = step.Name })
            : (WorkflowStep)new NoopStep("inner") { Name = step.Name };

        return new RetryStep(inner) { Name = step.Name };
    }

    /// <summary>
    /// Serialize a WorkflowStep tree to JSON (for DevUI display / testing).
    /// </summary>
    public static string ToJson(WorkflowStep step)
    {
        var obj = SerializeStep(step);
        return JsonSerializer.Serialize(obj, JsonOpts);
    }

    private static object SerializeStep(WorkflowStep step)
    {
        return step switch
        {
            HandoffStep hs => new
            {
                type = "handoff",
                name = hs.Name,
                agent = hs.SpecialistName,
            },
            SequentialStep ss => new
            {
                type = "sequential",
                name = ss.Name,
                count = ss.Count,
                steps = ss.Steps.Select(SerializeStep).ToList(),
            },
            ConcurrentStep cs => new
            {
                type = "concurrent",
                name = cs.Name,
                count = cs.Count,
                steps = cs.Steps.Select(SerializeStep).ToList(),
            },
            ConditionalStep cs => new
            {
                type = "conditional",
                name = cs.Name,
                condition = cs.Condition,
                trueStep = SerializeStep(cs.TrueStep),
                falseStep = SerializeStep(cs.FalseStep),
            },
            RetryStep rs => new
            {
                type = "retry",
                name = rs.Name,
                maxRetries = rs.MaxRetries,
                backoffMs = rs.BackoffMs,
                inner = SerializeStep(rs.Inner),
            },
            NoopStep ns => new
            {
                type = "noop",
                name = ns.Name,
                action = ns.Action,
            },
            _ => new { type = "unknown", name = step.Name },
        };
    }
}
