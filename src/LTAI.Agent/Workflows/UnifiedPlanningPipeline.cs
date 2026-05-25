using System.Diagnostics;
using LTAI.Core.Execution;
using LTAI.Planning;
using LTAI.Planning.Planning;
using LTAI.Planning.Quality;
using LTAI.Planning.Session;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// Unified planning pipeline: Intent → Decompose → Plan → Execute → Verify → Heal → Checkpoint.
/// 
/// Chains the previously orphaned components:
/// GovernorWorkflow → PlannerIntegration → DiffusionPlanner → 
/// TaskDecomposer → TaskPipeline → DecoupledExecutor → SelfHealer → CheckpointManager
/// </summary>
public sealed class UnifiedPlanningPipeline
{
    private readonly PlannerIntegration _plannerIntegration;
    private readonly PlannerCriticWorkflow _plannerCritic;
    private readonly TaskPipeline _taskPipeline;
    private readonly SelfHealer _selfHealer;
    private readonly TaskCheckpoint _checkpointManager;
    private readonly ILogger<UnifiedPlanningPipeline> _logger;

    public UnifiedPlanningPipeline(
        PlannerIntegration plannerIntegration,
        PlannerCriticWorkflow plannerCritic,
        TaskPipeline taskPipeline,
        SelfHealer selfHealer,
        TaskCheckpoint checkpointManager,
        ILogger<UnifiedPlanningPipeline> logger)
    {
        _plannerIntegration = plannerIntegration;
        _plannerCritic = plannerCritic;
        _taskPipeline = taskPipeline;
        _selfHealer = selfHealer;
        _checkpointManager = checkpointManager;
        _logger = logger;
    }

    /// <summary>
    /// Full pipeline: Decompose → Plan → Execute → Verify → Heal.
    /// Returns (finalResult, pipelineStatus).
    /// </summary>
    public async Task<PipelineResult> ExecuteAsync(
        string query, string intent, string domain,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<PipelineStep>();
        var sessionId = Guid.NewGuid().ToString("N")[..8];

        _logger.LogInformation("UnifiedPipeline [{Session}]: Starting for domain={Domain} intent={Intent}",
            sessionId, domain, intent);

        // Phase 1: Decompose + Plan
        try
        {
            steps.Add(new PipelineStep { Phase = "plan", Status = "running" });

            var plan = await _plannerIntegration.PlanAndExecuteAsync(
                intent, domain, query, cancellationToken: ct).ConfigureAwait(false);

            steps[^1] = steps[^1] with { Status = "done", Output = plan };
            _logger.LogInformation("UnifiedPipeline [{Session}]: Plan generated ({Len} chars)", sessionId, plan.Length);
        }
        catch (Exception ex)
        {
            steps[^1] = steps[^1] with { Status = "failed", Error = ex.Message };
            _logger.LogError(ex, "UnifiedPipeline [{Session}]: Plan phase failed", sessionId);
            return new PipelineResult { SessionId = sessionId, Success = false, Steps = steps, Error = ex.Message, TotalMs = sw.ElapsedMilliseconds };
        }

        // Phase 2: Execute via TaskPipeline (parallel subtasks via DecoupledExecutor)
        string executionResult = "";
        try
        {
            steps.Add(new PipelineStep { Phase = "execute", Status = "running" });

            executionResult = await _taskPipeline.ExecuteParallelAsync(
                query,
                async (task, innerCt) => string.IsNullOrEmpty(task) ? "" : task,
                ct).ConfigureAwait(false);

            steps[^1] = steps[^1] with { Status = "done", Output = executionResult };
        }
        catch (Exception ex)
        {
            steps[^1] = steps[^1] with { Status = "failed", Error = ex.Message };
        }

        // Phase 3: Verify via self-healing health check
        try
        {
            steps.Add(new PipelineStep { Phase = "verify", Status = "running" });
            var health = _selfHealer.GetStatus();
            var statusValue = health["status"]?.ToString();
            steps[^1] = steps[^1] with
            {
                Status = statusValue == "critical" ? "warning" : "done",
                Output = $"Health: {statusValue} (checks: {health["check_count"]})"
            };

            if (statusValue == "critical")
            {
                _logger.LogWarning("UnifiedPipeline [{Session}]: Health check CRITICAL, triggering heal", sessionId);
                await _selfHealer.HealCell("pipeline_cell").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            steps[^1] = steps[^1] with { Status = "warning", Error = ex.Message };
        }

        // Phase 4: Checkpoint
        try
        {
            steps.Add(new PipelineStep { Phase = "checkpoint", Status = "running" });
            await _checkpointManager.SaveAsync(sessionId,
                new LTAI.Planning.Models.CheckpointState
                {
                    SessionId = sessionId,
                    TaskGoal = query,
                    Plan = new List<string> { intent },
                    CompletedSteps = steps.Where(s => s.Status == "done").Select(s => s.Phase).ToList(),
                    CurrentStep = steps.LastOrDefault()?.Phase,
                    SavedAt = DateTime.UtcNow
                }).ConfigureAwait(false);
            steps[^1] = steps[^1] with { Status = "done" };
        }
        catch (Exception ex)
        {
            steps[^1] = steps[^1] with { Status = "warning", Error = ex.Message };
        }

        sw.Stop();
        _logger.LogInformation("UnifiedPipeline [{Session}]: Completed in {Ms}ms, {StepCount} steps",
            sessionId, sw.ElapsedMilliseconds, steps.Count);

        return new PipelineResult
        {
            SessionId = sessionId,
            Success = steps.All(s => s.Status != "failed"),
            Steps = steps,
            Result = executionResult,
            TotalMs = sw.ElapsedMilliseconds
        };
    }
}

public sealed record PipelineStep
{
    public string Phase { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Output { get; init; }
    public string? Error { get; init; }
}

public sealed record PipelineResult
{
    public string SessionId { get; init; } = "";
    public bool Success { get; init; }
    public List<PipelineStep> Steps { get; init; } = new();
    public string Result { get; init; } = "";
    public long TotalMs { get; init; }
    public string? Error { get; init; }
}
