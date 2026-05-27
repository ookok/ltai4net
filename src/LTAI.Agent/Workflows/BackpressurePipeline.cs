using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record BackpressurePipelineConfig
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan GateTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public bool FailFast { get; init; } = true;
    public bool AllowSkipOnConfig { get; init; } = true;
}

public sealed class BackpressurePipeline
{
    private readonly IEnumerable<IBackpressureGate> _gates;
    private readonly BackpressurePipelineConfig _config;
    private readonly ILogger<BackpressurePipeline> _logger;

    public BackpressurePipeline(
        IEnumerable<IBackpressureGate> gates,
        BackpressurePipelineConfig? config = null,
        ILogger<BackpressurePipeline>? logger = null)
    {
        _gates = gates.OrderBy(g => g.Order).ToList();
        _config = config ?? new BackpressurePipelineConfig();
        _logger = logger ?? NullLogger<BackpressurePipeline>.Instance;
    }

    public async Task<BackpressureResult> CheckAsync(
        string worktreePath,
        string agentId = "",
        string taskDescription = "",
        CancellationToken ct = default)
    {
        var allResults = new List<GateResult>();
        var attemptCount = 0;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        while (attemptCount < _config.MaxAttempts)
        {
            attemptCount++;
            var attemptResults = new List<GateResult>();
            var context = new BackpressureContext
            {
                WorktreePath = worktreePath,
                AgentId = agentId,
                TaskDescription = taskDescription,
                AttemptNumber = attemptCount,
                PreviousResults = allResults
            };

            bool anyRejection = false;

            foreach (var gate in _gates)
            {
                if (!gate.ShouldRun(context))
                {
                    _logger.LogDebug("Skipping gate {Gate} for attempt {Attempt}", gate.Name, attemptCount);
                    continue;
                }

                GateResult gateResult;
                using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                gateCts.CancelAfter(_config.GateTimeout);

                try
                {
                    gateResult = await gate.CheckAsync(worktreePath, gateCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    gateResult = new GateResult
                    {
                        Passed = false,
                        GateName = gate.Name,
                        Reason = $"Gate '{gate.Name}' timed out after {_config.GateTimeout.TotalSeconds}s",
                        ErrorCount = 1
                    };
                }

                attemptResults.Add(gateResult);
                allResults.Add(gateResult);

                if (!gateResult.Passed)
                {
                    anyRejection = true;
                    _logger.LogWarning("[{Gate}] REJECTED (attempt {Attempt}): {Reason}",
                        gate.Name, attemptCount, gateResult.Reason);

                    if (_config.FailFast)
                        break;
                }
                else
                {
                    _logger.LogDebug("[{Gate}] PASSED ({ElapsedMs}ms)", gate.Name, gateResult.ElapsedMs);
                }
            }

            if (!anyRejection)
            {
                totalSw.Stop();
                _logger.LogInformation("Backpressure PIPELINE PASSED (attempt {Attempt}, {TimeMs}ms)",
                    attemptCount, totalSw.ElapsedMilliseconds);

                return new BackpressureResult
                {
                    AllPassed = true,
                    AttemptCount = attemptCount,
                    GateResults = allResults,
                    TotalTime = totalSw.Elapsed,
                    Summary = $"All {_gates.Count()} gates passed on attempt {attemptCount} ({totalSw.ElapsedMilliseconds}ms)"
                };
            }

            if (attemptCount >= _config.MaxAttempts)
            {
                totalSw.Stop();
                var summary = $"Pipeline exhausted after {_config.MaxAttempts} attempt(s), {allResults.Count(g => !g.Passed)} gate(s) still failing";

                _logger.LogError("Backpressure PIPELINE FAILED: {Summary}", summary);

                return new BackpressureResult
                {
                    AllPassed = false,
                    AttemptCount = attemptCount,
                    GateResults = allResults,
                    TotalTime = totalSw.Elapsed,
                    Summary = summary
                };
            }

            _logger.LogInformation("Backpressure: retrying (attempt {Current}/{Max})...",
                attemptCount, _config.MaxAttempts);
        }

        totalSw.Stop();
        return new BackpressureResult
        {
            AllPassed = false,
            AttemptCount = attemptCount,
            GateResults = allResults,
            TotalTime = totalSw.Elapsed,
            Summary = "Pipeline exhausted with no pass"
        };
    }

    public IReadOnlyList<IBackpressureGate> Gates => _gates.ToList().AsReadOnly();
}
