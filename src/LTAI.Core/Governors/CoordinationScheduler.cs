using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public enum CoordinationEventType
{
    ParetoRouteChanged,
    BootstrapPhaseAdvanced,
    GenePoolEvolved,
    GeneDeployed,
    ArchitectProposal,
    ArchitectDeployed,
    CounterfactualBlock,
    LoopTrapDetected,
    MemoryPruned,
    SafetyAlert,
    BudgetExceeded,
    Custom
}

public sealed record CoordinationEvent
{
    public CoordinationEventType Type { get; init; }
    public string CustomEventType { get; init; } = "";
    public string Source { get; init; } = "";
    public string Payload { get; init; } = "";
    public Dictionary<string, string> Metadata { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public Guid Id { get; init; } = Guid.NewGuid();
}

public sealed record CoordinationSchedulerHealth
{
    public bool IsRunning { get; init; }
    public int QueueDepth { get; init; }
    public int EventsProcessed { get; init; }
    public int RulesTriggered { get; init; }
    public int RuleCount { get; init; }
    public int DynamicRuleCount { get; init; }
    public List<string> RegisteredDynamicTypes { get; init; } = new();
}

public sealed record CoordinationRule
{
    public CoordinationEventType Trigger { get; init; }
    public string CustomTrigger { get; init; } = "";
    public bool IsDynamicRule => !string.IsNullOrEmpty(CustomTrigger);
    public string[] TargetComponents { get; init; } = Array.Empty<string>();
    public int Priority { get; init; } = 50;
    public Func<CoordinationEvent, CancellationToken, Task> Handler { get; init; } = (_, _) => Task.CompletedTask;
    public bool Enabled { get; init; } = true;
    public TimeSpan? DebounceWindow { get; init; }
}

public sealed class CoordinationScheduler
{
    private readonly ConcurrentDictionary<CoordinationEventType, List<CoordinationRule>> _rules = new();
    private readonly ConcurrentDictionary<string, List<CoordinationRule>> _dynamicRules = new();
    private readonly ConcurrentDictionary<string, byte> _registeredDynamicTypes = new();
    private readonly ConcurrentQueue<CoordinationEvent> _eventQueue = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTriggered = new();
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<CoordinationScheduler> _logger;
    private Task? _dispatchLoop;
    private int _eventsProcessed;
    private int _rulesTriggered;

    public int EventsProcessed => _eventsProcessed;
    public int RulesTriggered => _rulesTriggered;
    public int RuleCount => _rules.Values.Sum(v => v.Count);
    public int QueueDepth => _eventQueue.Count;
    public bool IsRunning => _dispatchLoop is { IsCompleted: false };

    public CoordinationSchedulerHealth GetHealthReport()
    {
        return new CoordinationSchedulerHealth
        {
            IsRunning = IsRunning,
            QueueDepth = QueueDepth,
            EventsProcessed = _eventsProcessed,
            RulesTriggered = _rulesTriggered,
            RuleCount = RuleCount,
            DynamicRuleCount = _dynamicRules.Count,
            RegisteredDynamicTypes = _registeredDynamicTypes.Keys.ToList()
        };
    }

    public CoordinationScheduler(ILogger<CoordinationScheduler>? logger = null)
    {
        _logger = logger ?? NullLogger<CoordinationScheduler>.Instance;
    }

    public void Start()
    {
        _dispatchLoop = Task.Run(DispatchLoopAsync);
        _logger.LogInformation("CoordinationScheduler started");
    }

    public void Register(CoordinationRule rule)
    {
        if (rule.IsDynamicRule)
        {
            RegisterDynamic(rule.CustomTrigger, rule);
            return;
        }

        _rules.AddOrUpdate(rule.Trigger,
            _ => new List<CoordinationRule> { rule },
            (_, list) =>
            {
                list.Add(rule);
                return list;
            });
        _logger.LogDebug("Coordination rule registered: {Event} -> {Targets} (pri={Priority})",
            rule.Trigger, string.Join(",", rule.TargetComponents), rule.Priority);
    }

    public void RegisterDynamic(string eventType, CoordinationRule rule)
    {
        _registeredDynamicTypes.TryAdd(eventType, 0);
        _dynamicRules.AddOrUpdate(eventType,
            _ => new List<CoordinationRule> { rule },
            (_, list) =>
            {
                list.Add(rule);
                return list;
            });
        _logger.LogDebug("Coordination dynamic rule registered: '{Event}' -> {Targets} (pri={Priority})",
            eventType, string.Join(",", rule.TargetComponents), rule.Priority);
    }

    public void PublishDynamic(string eventType, string source, string payload, Dictionary<string, string>? metadata = null)
    {
        var evt = new CoordinationEvent
        {
            Type = CoordinationEventType.Custom,
            CustomEventType = eventType,
            Source = source,
            Payload = payload,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
        Publish(evt);
    }

    public IReadOnlyList<string> GetRegisteredDynamicTypes()
    {
        return _registeredDynamicTypes.Keys.ToList();
    }

    public void Publish(CoordinationEvent evt)
    {
        _eventQueue.Enqueue(evt);
        _logger.LogTrace("Coordination event queued: {Type} from {Source}", evt.Type, evt.Source);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_dispatchLoop != null)
            await _dispatchLoop.ConfigureAwait(false);
        _logger.LogInformation("CoordinationScheduler stopped (events={Events}, rules={Rules})",
            _eventsProcessed, _rulesTriggered);
    }

    private async Task DispatchLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_eventQueue.TryDequeue(out var evt))
                {
                    await DispatchAsync(evt, _cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CoordinationScheduler dispatch error");
            }
        }
    }

    private async Task DispatchAsync(CoordinationEvent evt, CancellationToken ct)
    {
        Interlocked.Increment(ref _eventsProcessed);

        var rules = new List<CoordinationRule>();

        if (_rules.TryGetValue(evt.Type, out var enumRules))
            rules.AddRange(enumRules);

        if (!string.IsNullOrEmpty(evt.CustomEventType) &&
            _dynamicRules.TryGetValue(evt.CustomEventType, out var dynamicRules))
            rules.AddRange(dynamicRules);

        if (rules.Count == 0) return;

        foreach (var rule in rules.OrderByDescending(r => r.Priority))
        {
            if (!rule.Enabled) continue;

            if (rule.DebounceWindow.HasValue)
            {
                var key = rule.IsDynamicRule ? $"{rule.CustomTrigger}:{string.Join(",", rule.TargetComponents)}" :
                    $"{rule.Trigger}:{string.Join(",", rule.TargetComponents)}";
                if (_lastTriggered.TryGetValue(key, out var last) &&
                    DateTime.UtcNow - last < rule.DebounceWindow.Value)
                    continue;
                _lastTriggered[key] = DateTime.UtcNow;
            }

            try
            {
                await rule.Handler(evt, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _rulesTriggered);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Coordination rule handler failed for {Event} -> {Targets}",
                    evt.Type, string.Join(",", rule.TargetComponents));
            }
        }
    }

    public void RegisterBootstrapRules(
        BootstrapTeacher teacher,
        GenePool genePool,
        SimulatedAnnealer annealer,
        ArchitectLoop architect)
    {
        Register(new CoordinationRule
        {
            Trigger = CoordinationEventType.BootstrapPhaseAdvanced,
            TargetComponents = new[] { "GenePool", "MemoryGraph" },
            Priority = 80,
            Handler = async (evt, ct) =>
            {
                _logger.LogInformation("[Coord] Phase advanced to {Phase}, triggering gene evolution",
                    evt.Payload);
                genePool.Evolve(eliteCount: 5, crossoverCount: 8, mutateCount: 12);
                if (teacher.Phase == BootstrapPhase.Autonomous)
                {
                    await annealer.StepAsync(proposalsPerEpoch: 5, ct: ct).ConfigureAwait(false);
                }
            }
        });

        Register(new CoordinationRule
        {
            Trigger = CoordinationEventType.GeneDeployed,
            TargetComponents = new[] { "ParetoRouter", "ArchitectLoop" },
            Priority = 70,
            Handler = async (evt, ct) =>
            {
                _logger.LogInformation("[Coord] Genes deployed, requesting architect review");
                await architect.DiagnoseAsync(ct).ConfigureAwait(false);
            }
        });

        Register(new CoordinationRule
        {
            Trigger = CoordinationEventType.ArchitectProposal,
            TargetComponents = new[] { "CounterfactualGate" },
            Priority = 90,
            DebounceWindow = TimeSpan.FromMinutes(2),
            Handler = (evt, ct) =>
            {
                _logger.LogInformation("[Coord] Architect proposal received: {Payload}", evt.Payload);
                return Task.CompletedTask;
            }
        });

        Register(new CoordinationRule
        {
            Trigger = CoordinationEventType.LoopTrapDetected,
            TargetComponents = new[] { "BootstrapTeacher", "ParetoRouter" },
            Priority = 95,
            Handler = async (evt, ct) =>
            {
                _logger.LogWarning("[Coord] Loop trap detected, boosting curiosity budget + jitter");
                await teacher.FeedCuriosityBudgetAsync(5.0, ct).ConfigureAwait(false);
            }
        });
    }
}
