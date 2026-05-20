using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Sandbox;

public enum RecipePhase { SFT, RL, Evaluation, Complete, Failed }

public sealed class RecipeStep
{
    public string Name { get; init; } = "";
    public RecipePhase Phase { get; init; }
    public string Description { get; init; } = "";
    public int MaxIterations { get; init; } = 10;
    public double TargetScore { get; init; }
    public Func<ISandbox, CancellationToken, Task<RecipeStepResult>>? Execute { get; init; }
}

public sealed class RecipeStepResult
{
    public bool Success { get; init; }
    public double Score { get; init; }
    public string Output { get; init; } = "";
    public Dictionary<string, object> Metrics { get; init; } = new();
}

public sealed class TrainingRecipe
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<RecipeStep> Steps { get; init; } = new();
}

public sealed class TrainingRecipePipeline
{
    private readonly IEnumerable<ISandbox> _sandboxes;
    private readonly ILogger<TrainingRecipePipeline> _logger;
    private readonly ConcurrentDictionary<string, RecipeRun> _runs = new();
    private int _runCounter;

    public TrainingRecipePipeline(IEnumerable<ISandbox> sandboxes, ILogger<TrainingRecipePipeline>? logger = null)
    {
        _sandboxes = sandboxes;
        _logger = logger ?? NullLogger<TrainingRecipePipeline>.Instance;
    }

    public async Task<RecipeRunReport> RunAsync(TrainingRecipe recipe, CancellationToken ct = default)
    {
        var runId = $"recipe-{Interlocked.Increment(ref _runCounter)}";
        var run = new RecipeRun { Id = runId, RecipeName = recipe.Name, StartedAt = DateTime.UtcNow };
        _runs[runId] = run;

        var sandbox = _sandboxes.FirstOrDefault(s => s.IsAvailableAsync(ct).Result);
        if (sandbox == null)
        {
            run.Phase = RecipePhase.Failed;
            run.Error = "No sandbox available";
            return run.ToReport();
        }

        foreach (var step in recipe.Steps)
        {
            if (ct.IsCancellationRequested)
            {
                run.Phase = RecipePhase.Failed;
                run.Error = "Cancelled";
                return run.ToReport();
            }

            run.CurrentStep = step.Name;
            run.CurrentPhase = step.Phase;

            for (int iter = 0; iter < step.MaxIterations; iter++)
            {
                try
                {
                    var result = step.Execute != null
                        ? await step.Execute(sandbox, ct)
                        : new RecipeStepResult { Success = true, Score = 1.0 };

                    run.StepResults.Add(new StepRun
                    {
                        StepName = step.Name,
                        Iteration = iter,
                        Success = result.Success,
                        Score = result.Score,
                        Output = result.Output,
                        Metrics = result.Metrics
                    });

                    if (result.Score >= step.TargetScore)
                    {
                        _logger.LogInformation("Recipe step {Step}/{Iter} reached target {Score:F2}",
                            step.Name, iter, result.Score);
                        break;
                    }

                    if (iter == step.MaxIterations - 1)
                        _logger.LogWarning("Recipe step {Step} max iterations reached, score: {Score:F2}",
                            step.Name, result.Score);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Recipe step {Step} iteration {Iter} failed", step.Name, iter);
                    run.StepResults.Add(new StepRun
                    {
                        StepName = step.Name, Iteration = iter,
                        Success = false, Output = ex.Message
                    });
                }
            }

            var bestScore = run.StepResults.Where(s => s.StepName == step.Name)
                .Select(s => s.Score).DefaultIfEmpty(0).Max();

            if (bestScore < step.TargetScore && step.TargetScore > 0)
            {
                run.Phase = RecipePhase.Failed;
                run.Error = $"Step '{step.Name}' did not reach target score ({bestScore:F2} < {step.TargetScore:F2})";
                run.CompletedAt = DateTime.UtcNow;
                return run.ToReport();
            }
        }

        run.Phase = RecipePhase.Complete;
        run.CompletedAt = DateTime.UtcNow;
        _logger.LogInformation("Recipe {Name} completed successfully in {Steps} steps",
            recipe.Name, recipe.Steps.Count);

        return run.ToReport();
    }

    public RecipeRunReport? GetRun(string runId)
    {
        return _runs.TryGetValue(runId, out var run) ? run.ToReport() : null;
    }

    public List<RecipeRunReport> ListRuns()
        => _runs.Values.Select(r => r.ToReport()).OrderByDescending(r => r.StartedAt).ToList();

    public static TrainingRecipe CreateSWERecipe()
    {
        return new TrainingRecipe
        {
            Name = "Orchard-SWE",
            Description = "Software Engineering agent training: SFT → RL → Eval",
            Steps = new List<RecipeStep>
            {
                new()
                {
                    Name = "SFT-Training",
                    Phase = RecipePhase.SFT,
                    Description = "Supervised fine-tuning on SWE trajectories",
                    MaxIterations = 5,
                    TargetScore = 0.60
                },
                new()
                {
                    Name = "RL-Training",
                    Phase = RecipePhase.RL,
                    Description = "On-policy RL with sandbox-verified rewards",
                    MaxIterations = 10,
                    TargetScore = 0.65
                },
                new()
                {
                    Name = "SWE-bench-Eval",
                    Phase = RecipePhase.Evaluation,
                    Description = "Evaluation on SWE-bench Verified",
                    MaxIterations = 1,
                    TargetScore = 0
                }
            }
        };
    }

    public static TrainingRecipe CreateGUIRecipe()
    {
        return new TrainingRecipe
        {
            Name = "Orchard-GUI",
            Description = "Browser navigation agent: SFT → RL → Eval",
            Steps = new List<RecipeStep>
            {
                new() { Name = "GUI-SFT", Phase = RecipePhase.SFT, MaxIterations = 5, TargetScore = 0.65 },
                new() { Name = "GUI-RL", Phase = RecipePhase.RL, MaxIterations = 10, TargetScore = 0.68 },
                new() { Name = "GUI-Eval", Phase = RecipePhase.Evaluation, MaxIterations = 1, TargetScore = 0 }
            }
        };
    }
}

internal sealed class RecipeRun
{
    public string Id { get; init; } = "";
    public string RecipeName { get; init; } = "";
    public RecipePhase Phase { get; set; } = RecipePhase.SFT;
    public RecipePhase CurrentPhase { get; set; }
    public string CurrentStep { get; set; } = "";
    public string? Error { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<StepRun> StepResults { get; init; } = new();

    public RecipeRunReport ToReport() => new()
    {
        Id = Id, RecipeName = RecipeName, Phase = Phase,
        CurrentStep = CurrentStep, Error = Error,
        StartedAt = StartedAt, CompletedAt = CompletedAt,
        TotalSteps = StepResults.Count,
        TotalSuccesses = StepResults.Count(s => s.Success),
        BestScores = StepResults.GroupBy(s => s.StepName)
            .ToDictionary(g => g.Key, g => g.Max(s => s.Score))
    };
}

public sealed class StepRun
{
    public string StepName { get; init; } = "";
    public int Iteration { get; init; }
    public bool Success { get; init; }
    public double Score { get; init; }
    public string Output { get; init; } = "";
    public Dictionary<string, object> Metrics { get; init; } = new();
}

public sealed class RecipeRunReport
{
    public string Id { get; init; } = "";
    public string RecipeName { get; init; } = "";
    public RecipePhase Phase { get; init; }
    public string CurrentStep { get; init; } = "";
    public string? Error { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int TotalSteps { get; init; }
    public int TotalSuccesses { get; init; }
    public Dictionary<string, double> BestScores { get; init; } = new();
}
