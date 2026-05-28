using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record ArchitectureMetric
{
    public string Key { get; init; } = "";
    public double Value { get; init; }
    public double Threshold { get; init; }
    public string Status { get; init; } = ""; // "ok", "warning", "critical"
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

public sealed record ArchitectureDiagnosis
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Issue { get; init; } = "";
    public string RootCause { get; init; } = "";
    public double Severity { get; init; } // 0-1
    public List<string> AffectedComponents { get; init; } = new();
    public DateTime DiagnosedAt { get; init; } = DateTime.UtcNow;
}

public sealed record ArchitectureProposal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string DiagnosisId { get; init; } = "";
    public string Description { get; init; } = "";
    public ArchitectureAction Action { get; init; }
    public string TargetComponent { get; init; } = "";
    public Dictionary<string, object> Parameters { get; init; } = new();
    public double ExpectedImprovement { get; init; }
    public double Risk { get; init; } // 0-1
    public string RollbackStrategy { get; init; } = "revert_to_snapshot";
    public DateTime ProposedAt { get; init; } = DateTime.UtcNow;
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
}

public enum ArchitectureAction
{
    MutateGene,
    AddParetoPoint,
    RemoveParetoPoint,
    AdjustShadowRate,
    AdvancePhase,
    AddSeedGene,
    DeployTopGenes,
    RefillCuriosity,
    AdjustTemperature,
    TriggerEvolution,
    CompactFrontier,
    ResetComponent,
    UpdateIntentKeywords,
    PersistRules,
    AdjustAnchorPhase,
    AdjustAnchorGamma,
    AdjustTeachingQuota,
    AdjustAccuracyThreshold,
    AdjustShadowingQuota,
    AdjustShadowingThreshold,
    DeployAgent,
    UndeployAgent,
    HotSwapAgent
}

public enum ProposalStatus
{
    Pending,
    Approved,
    Deployed,
    Rejected,
    RolledBack,
    Errored
}

public sealed record DeploymentSnapshot
{
    public string ProposalId { get; init; } = "";
    public List<ParetoPoint> FrontierBefore { get; init; } = new();
    public List<(string Id, double Fitness)> GeneFitnessBefore { get; init; } = new();
    public BootstrapPhase PhaseBefore { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
}

public sealed class ArchitectLoop
{
    private readonly ParetoRouter _router;
    private readonly BootstrapTeacher _teacher;
    private readonly GenePool _genePool;
    private readonly SimulatedAnnealer _annealer;
    private readonly GeneToRule _geneToRule;
    private readonly Func<string, CancellationToken, Task<string>> _l2Architect;
    private readonly CounterfactualGate _counterfactualGate;
    private readonly L0IntentClassifier? _intentClassifier;
    private readonly SemanticAnchor? _semanticAnchor;
    private readonly SemanticDiffAgent? _diffAgent;
    private readonly IServiceProvider? _serviceProvider;
    private readonly RecursiveCausalAudit? _causalAudit;
    private readonly ILogger<ArchitectLoop> _logger;

    private readonly ConcurrentDictionary<string, IAgent> _deployedAgents = new();

    private readonly ConcurrentQueue<ArchitectureDiagnosis> _diagnosisHistory = new();
    private readonly ConcurrentQueue<ArchitectureProposal> _proposalHistory = new();
    private readonly List<ArchitectureMetric> _currentMetrics = new();
    private readonly object _metricLock = new();
    private readonly ConcurrentDictionary<string, DeploymentSnapshot> _snapshots = new();

    private DateTime _lastLoop;
    private readonly TimeSpan _minLoopInterval;
    private int _loopCount;
    private int _deployedCount;
    private int _rolledBackCount;
    private int _consecutiveRejects;

    public int LoopCount => _loopCount;
    public int DeployedCount => _deployedCount;
    public int RolledBackCount => _rolledBackCount;

    public ArchitectLoop(
        ParetoRouter router,
        BootstrapTeacher teacher,
        GenePool genePool,
        SimulatedAnnealer annealer,
        GeneToRule geneToRule,
        Func<string, CancellationToken, Task<string>> l2Architect,
        CounterfactualGate? counterfactualGate = null,
        TimeSpan? minLoopInterval = null,
        L0IntentClassifier? intentClassifier = null,
        SemanticAnchor? semanticAnchor = null,
        SemanticDiffAgent? diffAgent = null, // TODO: make required after test suites updated
        IServiceProvider? serviceProvider = null,
        RecursiveCausalAudit? causalAudit = null,
        ILogger<ArchitectLoop>? logger = null)
    {
        _router = router;
        _teacher = teacher;
        _genePool = genePool;
        _annealer = annealer;
        _geneToRule = geneToRule;
        _counterfactualGate = counterfactualGate ?? new CounterfactualGate();
        _l2Architect = l2Architect;
        _intentClassifier = intentClassifier;
        _semanticAnchor = semanticAnchor;
        _diffAgent = diffAgent;
        _serviceProvider = serviceProvider;
        _causalAudit = causalAudit;
        _minLoopInterval = minLoopInterval ?? TimeSpan.FromMinutes(5);
        _logger = logger ?? NullLogger<ArchitectLoop>.Instance;
        _lastLoop = DateTime.MinValue;
    }

    public async Task<ArchitectureProposal?> RunAsync(CancellationToken ct = default)
    {
        var elapsed = DateTime.UtcNow - _lastLoop;
        if (elapsed < _minLoopInterval)
        {
            _logger.LogDebug("Skipping architecture loop (elapsed={Elapsed}s < min={Min}s)",
                elapsed.TotalSeconds, _minLoopInterval.TotalSeconds);
            return null;
        }

        Interlocked.Increment(ref _loopCount);
        _lastLoop = DateTime.UtcNow;

        _logger.LogInformation("=== Architecture Loop #{Loop} ===", _loopCount);

        var diagnosis = await DiagnoseAsync(ct).ConfigureAwait(false);
        if (diagnosis == null)
        {
            _logger.LogInformation("Architecture loop #{Loop}: no issues detected", _loopCount);
            return null;
        }

        var proposal = await ProposeAsync(diagnosis, ct).ConfigureAwait(false);
        if (proposal == null)
        {
            _logger.LogInformation("Architecture loop #{Loop}: no viable proposal", _loopCount);
            return null;
        }

        await DeployAsync(proposal, ct).ConfigureAwait(false);

        return proposal;
    }

    public async Task<ArchitectureDiagnosis?> DiagnoseAsync(CancellationToken ct = default)
    {
        CollectMetrics();

        var prompt = BuildDiagnosisPrompt();
        if (string.IsNullOrEmpty(prompt)) return null;

        try
        {
            var response = await _l2Architect(prompt, ct).ConfigureAwait(false);
            var diagnosis = ParseDiagnosis(response);

            if (diagnosis != null)
            {
                if (_causalAudit != null)
                {
                    try
                    {
                        var auditResult = await _causalAudit.AuditAsync(
                            prompt, response, $"issue={diagnosis.Issue} cause={diagnosis.RootCause}", ct).ConfigureAwait(false);
                        if (!auditResult.Passed)
                        {
                            _logger.LogWarning("Diagnosis #{D} causal audit FAILED: {Violations} — discarding",
                                diagnosis.Id, string.Join("; ", auditResult.Violations.Take(3)));
                            return null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Causal audit skipped for diagnosis #{D}", diagnosis.Id);
                    }
                }

                _diagnosisHistory.Enqueue(diagnosis);
                while (_diagnosisHistory.Count > 50)
                    _diagnosisHistory.TryDequeue(out _);

                _logger.LogWarning("Diagnosis #{D}: {Issue} (severity={Severity:F2}, cause={Root})",
                    diagnosis.Id, diagnosis.Issue, diagnosis.Severity, diagnosis.RootCause);
            }

            return diagnosis;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Architecture diagnosis failed");
            return null;
        }
    }

    public async Task<ArchitectureProposal?> ProposeAsync(ArchitectureDiagnosis diagnosis, CancellationToken ct = default)
    {
        var prompt = BuildProposalPrompt(diagnosis);
        if (string.IsNullOrEmpty(prompt)) return null;

        try
        {
            var response = await _l2Architect(prompt, ct).ConfigureAwait(false);
            var proposal = ParseProposal(response, diagnosis.Id);

            if (proposal != null)
            {
                bool rejected = false;

                if (proposal.Risk > 0.7)
                {
                    _logger.LogWarning("Proposal rejected: risk too high ({Risk:F2}): {Desc}",
                        proposal.Risk, proposal.Description);
                    proposal.Status = ProposalStatus.Rejected;
                    rejected = true;
                }
                else if (proposal.ExpectedImprovement < 0.05)
                {
                    _logger.LogInformation("Proposal rejected: improvement too low ({Improv:F3})",
                        proposal.ExpectedImprovement);
                    proposal.Status = ProposalStatus.Rejected;
                    rejected = true;
                }
                else
                {
                    proposal.Status = ProposalStatus.Approved;
                }

                if (rejected)
                {
                    var rejects = Interlocked.Increment(ref _consecutiveRejects);
                    if (rejects >= 10)
                    {
                        _logger.LogWarning(
                            "Architect: {Count} consecutive rejects — triggering bootstrap reset to break learned helplessness",
                            rejects);
                        await _teacher.ResetAsync(ct).ConfigureAwait(false);
                        Interlocked.Exchange(ref _consecutiveRejects, 0);
                        await _teacher.FeedCuriosityBudgetAsync(50.0, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    Interlocked.Exchange(ref _consecutiveRejects, 0);
                }

                _proposalHistory.Enqueue(proposal);
                while (_proposalHistory.Count > 100)
                    _proposalHistory.TryDequeue(out _);

                _logger.LogInformation("Proposal {P}: {Desc} (action={Action}, risk={Risk:F2}, improve={Improv:F2})",
                    proposal.Id, proposal.Description, proposal.Action, proposal.Risk, proposal.ExpectedImprovement);
            }

            return proposal;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Architecture proposal failed");
            return null;
        }
    }

    public async Task<bool> DeployAsync(ArchitectureProposal proposal, CancellationToken ct = default)
    {
        if (proposal.Status != ProposalStatus.Approved) return false;

        if (proposal.Risk > 0.7)
        {
            _logger.LogWarning("[HITL-REQUIRED] High-risk proposal {P}: risk={Risk:F2}, action={Action}, desc={Desc} — human approval needed before deployment",
                proposal.Id, proposal.Risk, proposal.Action, proposal.Description);

            if (_serviceProvider != null)
            {
                try
                {
                    var hitlType = Type.GetType("LTAI.Agent.Workflows.HumanInTheLoopReview, LTAI.Agent");
                    if (hitlType == null)
                    {
                        _logger.LogWarning("HITL review unavailable: LTAI.Agent assembly not loaded — Risk={Risk} proposal {P} cannot be reviewed",
                            proposal.Risk, proposal.Id);
                    }
                    else
                    {
                        var hitl = hitlType.GetMethod("CreateReviewTask")?.Invoke(
                            _serviceProvider.GetService(hitlType),
                            new object[] { "ArchitectLoop", $"Risk={proposal.Risk:F2}: {proposal.Description}",
                                1.0 - proposal.Risk, null!, TimeSpan.FromHours(1), 2, proposal.Risk });
                        _logger.LogInformation("HITL: review submitted for proposal {P}", proposal.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HITL review failed for proposal {P}", proposal.Id);
                }
            }

            proposal.Status = ProposalStatus.Pending;
            return false;
        }

        if (proposal.Risk > 0.3)
        {
            var shadowRouter = _counterfactualGate.CloneRouter(_router);
            ApplyProposalToShadow(proposal, shadowRouter);

            var cfResult = _counterfactualGate.Evaluate(_router, shadowRouter);
            if (!cfResult.Passed)
            {
                _logger.LogWarning("Counterfactual BLOCKED proposal {P}: {Reason}",
                    proposal.Id, cfResult.Reason);
                proposal.Status = ProposalStatus.Rejected;
                return false;
            }

            _logger.LogInformation("Counterfactual PASSED for proposal {P} (shift={Shift:F3}, regret={Regret:F3})",
                proposal.Id, cfResult.DistributionShift, cfResult.RegretScore);
        }

        // SemanticDiffAgent gate — REQUIRED for production safety.
        // If null (test environment), log CRITICAL warning.
        if (_diffAgent == null)
        {
            _logger.LogWarning("CRITICAL: SemanticDiffAgent is null — proposal {P} proceeds WITHOUT semantic safety check!",
                proposal.Id);
        }
        else
        {
            var safetyResult = _diffAgent.EvaluateProposal(proposal);
            if (!safetyResult.Safe)
            {
                _logger.LogWarning("SemanticDiff BLOCKED proposal {P}: {Reason}",
                    proposal.Id, safetyResult.Reason);
                proposal.Status = ProposalStatus.Rejected;
                return false;
            }
        }

        try
        {
            var snapshot = CaptureSnapshot(proposal.Id);
            _snapshots[proposal.Id] = snapshot;

            _logger.LogInformation("Deploying proposal {P}: {Desc}", proposal.Id, proposal.Description);

            switch (proposal.Action)
            {
                case ArchitectureAction.MutateGene:
                    await Task.Run(() =>
                        _genePool.Evolve(eliteCount: 2, crossoverCount: 3, mutateCount: 5), ct)
                        .ConfigureAwait(false);
                    break;

                case ArchitectureAction.AddParetoPoint:
                    if (proposal.Parameters.TryGetValue("quality", out var q) &&
                        proposal.Parameters.TryGetValue("speed", out var s) &&
                        proposal.Parameters.TryGetValue("cost", out var c) &&
                        proposal.Parameters.TryGetValue("label", out var label))
                    {
                        _router.AddFrontierPoint(new ParetoPoint
                        {
                            Label = label?.ToString() ?? "",
                            Quality = Convert.ToSingle(q),
                            Speed = Convert.ToSingle(s),
                            Cost = Convert.ToSingle(c)
                        });
                    }
                    break;

                case ArchitectureAction.RemoveParetoPoint:
                    await Task.Run(() => _router.PruneDominated(), ct).ConfigureAwait(false);
                    break;

                case ArchitectureAction.AdjustShadowRate:
                    if (proposal.Parameters.TryGetValue("rate", out var rate))
                    {
                        var newRate = Math.Clamp(Convert.ToSingle(rate), 0.01f, 0.50f);
                        _router.SetShadowRate(newRate);
                        _logger.LogInformation("Shadow rate adjusted to {Rate:P0}", newRate);
                    }
                    break;

                case ArchitectureAction.AdvancePhase:
                    await _teacher.AdvancePhaseIfReadyAsync(ct).ConfigureAwait(false);
                    break;

                case ArchitectureAction.AddSeedGene:
                    if (proposal.Parameters.TryGetValue("condition", out var cond) &&
                        proposal.Parameters.TryGetValue("action", out var act))
                    {
                        var seedGene = new Gene
                        {
                            Condition = cond?.ToString() ?? "",
                            Action = act?.ToString() ?? "",
                            Weight = 1.0
                        };

                        if (proposal.Parameters.TryGetValue("route_label", out var rl) && rl is string rls)
                            seedGene = seedGene with { RouteLabel = rls };

                        if (proposal.Parameters.TryGetValue("condition_labels", out var cl) && cl is string cls)
                            seedGene = seedGene with
                            {
                                ConditionLabels = cls.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim()).ToList()
                            };

                        _genePool.Seed(new[] { seedGene });
                    }
                    break;

                case ArchitectureAction.DeployTopGenes:
                    await _geneToRule.DeployTopGenesAsync(
                        proposal.Parameters.TryGetValue("count", out var cnt) ? Convert.ToInt32(cnt) : 5,
                        ct).ConfigureAwait(false);
                    if (_intentClassifier != null)
                        _geneToRule.SyncKeywordsToClassifier(_intentClassifier);
                    break;

                case ArchitectureAction.RefillCuriosity:
                    if (proposal.Parameters.TryGetValue("amount", out var amount))
                        await _teacher.FeedCuriosityBudgetAsync(Convert.ToDouble(amount), ct).ConfigureAwait(false);
                    break;

                case ArchitectureAction.AdjustTemperature:
                    if (proposal.Parameters.TryGetValue("temperature", out var temp))
                    {
                        var newTemp = Math.Clamp(Convert.ToDouble(temp), 0.001, 2.0);
                        _annealer.SetTemperature(newTemp);
                        _logger.LogInformation("Annealer temperature adjusted to {Temp:F4}", newTemp);
                    }
                    break;

                case ArchitectureAction.TriggerEvolution:
                    await Task.Run(() =>
                        _genePool.Evolve(
                            proposal.Parameters.TryGetValue("generations", out var gen) ? Convert.ToInt32(gen) : 2,
                            mutateCount: 10),
                        ct).ConfigureAwait(false);
                    break;

                case ArchitectureAction.CompactFrontier:
                    _geneToRule.ExtractRulesFromFrontier();
                    break;

                case ArchitectureAction.UpdateIntentKeywords:
                    if (_intentClassifier != null &&
                        proposal.Parameters.TryGetValue("domain", out var dom) &&
                        proposal.Parameters.TryGetValue("keywords", out var kwsStr))
                    {
                        var domain = dom?.ToString() ?? "general";
                        var keywords = (kwsStr?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(k => k.Trim()).Where(k => k.Length > 1).ToArray();
                        var qi = proposal.Parameters.TryGetValue("quality", out var iq) ? Convert.ToSingle(iq) : 0.75f;
                        var si = proposal.Parameters.TryGetValue("speed", out var ist) ? Convert.ToSingle(ist) : 0.5f;
                        var ci = proposal.Parameters.TryGetValue("cost", out var ict) ? Convert.ToSingle(ict) : 0.5f;

                        _intentClassifier.HotUpdateKeywords(domain, keywords, qi, si, ci);
                        _logger.LogInformation("Intent keywords updated: domain '{Domain}' +{Count} keywords",
                            domain, keywords.Length);
                    }
                    break;

                case ArchitectureAction.PersistRules:
                    if (_intentClassifier != null)
                        await _intentClassifier.PersistRulesAsync(ct).ConfigureAwait(false);
                    break;

                case ArchitectureAction.AdjustAnchorPhase:
                    if (_semanticAnchor != null &&
                        proposal.Parameters.TryGetValue("phase_threshold", out var pt))
                        _semanticAnchor.SetPhaseThreshold(Convert.ToSingle(pt));
                    break;

                case ArchitectureAction.AdjustAnchorGamma:
                    if (_semanticAnchor != null &&
                        proposal.Parameters.TryGetValue("gamma", out var ag))
                        _semanticAnchor.SetAdaptiveGamma(Convert.ToSingle(ag));
                    break;

                case ArchitectureAction.AdjustTeachingQuota:
                    if (proposal.Parameters.TryGetValue("quota", out var tq))
                    {
                        var newQuota = Math.Clamp(Convert.ToInt32(tq), 100, 10000);
                        _teacher.TeachingQuota = newQuota;
                        _logger.LogInformation("TeachingQuota adjusted to {Quota}", newQuota);
                    }
                    break;

                case ArchitectureAction.AdjustAccuracyThreshold:
                    if (proposal.Parameters.TryGetValue("accuracy", out var at))
                    {
                        var newAcc = Math.Clamp(Convert.ToDouble(at), 0.5, 0.99);
                        _teacher.TeachingAccuracyThreshold = newAcc;
                        _teacher.ShadowingAccuracyThreshold = Math.Min(0.99, newAcc + 0.10);
                        _logger.LogInformation("Accuracy thresholds adjusted: teaching={T:F3}, shadowing={S:F3}",
                            newAcc, _teacher.ShadowingAccuracyThreshold);
                    }
                    break;

                case ArchitectureAction.AdjustShadowingQuota:
                    if (proposal.Parameters.TryGetValue("extra_quota", out var sq))
                    {
                        var newQuota = Math.Clamp(Convert.ToInt32(sq), 100, 5000);
                        _teacher.ShadowingExtraQueries = newQuota;
                        _logger.LogInformation("ShadowingExtraQueries adjusted to {Quota}", newQuota);
                    }
                    break;

                case ArchitectureAction.AdjustShadowingThreshold:
                    if (proposal.Parameters.TryGetValue("threshold", out var st))
                    {
                        var newThresh = Math.Clamp(Convert.ToDouble(st), 0.80, 0.99);
                        _teacher.ShadowingAccuracyThreshold = newThresh;
                        _logger.LogInformation("ShadowingAccuracyThreshold adjusted to {Threshold:F3}", newThresh);
                    }
                    break;

                case ArchitectureAction.DeployAgent:
                    if (_serviceProvider != null &&
                        proposal.Parameters.TryGetValue("niche", out var niche) &&
                        proposal.Parameters.TryGetValue("factory_id", out var factoryId))
                    {
                        var nicheStr = niche?.ToString() ?? "general";
                        var factories = _serviceProvider.GetServices<IAgentFactory>();
                        var factory = factories.FirstOrDefault(f =>
                            string.Equals(f.FactoryId, factoryId?.ToString(), StringComparison.OrdinalIgnoreCase));
                        if (factory == null)
                        {
                            _logger.LogWarning("DeployAgent: factory '{FactoryId}' not found", factoryId);
                            break;
                        }
                        var config = new Dictionary<string, object> { ["niche"] = nicheStr };
                        var deployed = await factory.CreateAsync(config, ct).ConfigureAwait(false);
                        await deployed.ActivateAsync(ct).ConfigureAwait(false);
                        _deployedAgents[deployed.AgentId] = deployed;
                        PublishAgentEvent("agent.deployed", deployed.AgentId, deployed.Niche);
                        _logger.LogInformation("DeployAgent: deployed {AgentId} ({Niche}) via {FactoryId}",
                            deployed.AgentId, deployed.Niche, factory.FactoryId);
                    }
                    else
                    {
                        _logger.LogWarning("DeployAgent: missing serviceProvider or parameters");
                    }
                    break;

                case ArchitectureAction.UndeployAgent:
                    if (proposal.Parameters.TryGetValue("agent_id", out var agentId) &&
                        _deployedAgents.TryRemove(agentId?.ToString() ?? "", out var removed))
                    {
                        await removed.DeactivateAsync(ct).ConfigureAwait(false);
                        PublishAgentEvent("agent.undeployed", removed.AgentId, removed.Niche);
                        _logger.LogInformation("UndeployAgent: deactivated {AgentId}", removed.AgentId);
                    }
                    else
                    {
                        _logger.LogWarning("UndeployAgent: agent '{AgentId}' not found",
                            proposal.Parameters.GetValueOrDefault("agent_id"));
                    }
                    break;

                case ArchitectureAction.HotSwapAgent:
                    if (proposal.Parameters.TryGetValue("agent_id", out var swapId) &&
                        proposal.Parameters.TryGetValue("factory_id", out var swapFactoryId) &&
                        _serviceProvider != null)
                    {
                        var idStr = swapId?.ToString() ?? "";

                        // Create new agent FIRST — creation failure leaves old agent running
                        var factories = _serviceProvider.GetServices<IAgentFactory>();
                        var factory = factories.FirstOrDefault(f =>
                            string.Equals(f.FactoryId, swapFactoryId?.ToString(), StringComparison.OrdinalIgnoreCase));
                        if (factory == null)
                        {
                            _logger.LogWarning("HotSwapAgent: factory '{Factory}' not found", swapFactoryId);
                            break;
                        }

                        var config = new Dictionary<string, object>
                        {
                            ["niche"] = proposal.Parameters.GetValueOrDefault("niche")?.ToString() ?? "general"
                        };
                        var newAgent = await factory.CreateAsync(config, ct).ConfigureAwait(false);
                        await newAgent.ActivateAsync(ct).ConfigureAwait(false);

                        // NOW remove old agent (safe — new one is ready)
                        if (_deployedAgents.TryRemove(idStr, out var oldAgent))
                        {
                            try { await oldAgent.DeactivateAsync(ct).ConfigureAwait(false); }
                            catch (Exception ex) { _logger.LogWarning(ex, "HotSwapAgent: old agent deactivation failed (non-fatal)"); }
                        }

                        _deployedAgents[newAgent.AgentId] = newAgent;
                        _logger.LogInformation("HotSwapAgent: {OldId} → {NewId} via {FactoryId}",
                            idStr, newAgent.AgentId, factory.FactoryId);
                    }
                    else
                    {
                        _logger.LogWarning("HotSwapAgent: missing parameters or serviceProvider");
                    }
                    break;
            }

            proposal.Status = ProposalStatus.Deployed;
            Interlocked.Increment(ref _deployedCount);
            _logger.LogInformation("Deployed proposal {P} successfully", proposal.Id);
            return true;
        }
        catch (Exception ex)
        {
            proposal.Status = ProposalStatus.Errored;
            _logger.LogError(ex, "Failed to deploy proposal {P}: {Desc}", proposal.Id, proposal.Description);
            return false;
        }
    }

    public async Task<bool> RollbackAsync(ArchitectureProposal proposal, CancellationToken ct = default)
    {
        _logger.LogWarning("Rolling back proposal {P}: {Desc}", proposal.Id, proposal.Description);

        if (_snapshots.TryGetValue(proposal.Id, out var snapshot))
        {
            try
            {
                var currentFrontier = _router.GetFrontier();
                var snapshotIds = new HashSet<string>(snapshot.FrontierBefore.Select(p => p.Id));
                foreach (var p in currentFrontier.Where(p => !snapshotIds.Contains(p.Id)))
                    _router.RemoveFrontierPoint(p.Id);
                foreach (var sp in snapshot.FrontierBefore)
                    _router.AddFrontierPoint(sp with { });

                foreach (var (geneId, fitness) in snapshot.GeneFitnessBefore)
                    _genePool.UpdateFitness(geneId, fitness);

                if (snapshot.PhaseBefore != _teacher.Phase)
                    await _teacher.ForceAdvancePhaseAsync(snapshot.PhaseBefore, ct).ConfigureAwait(false);

                _snapshots.TryRemove(proposal.Id, out _);

                _logger.LogInformation("Rollback complete for proposal {P}: restored frontier ({Count} pts) + gene fitness + phase ({Phase})",
                    proposal.Id, snapshot.FrontierBefore.Count, snapshot.PhaseBefore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rollback partial failure for proposal {P}", proposal.Id);
            }
        }
        else
        {
            _logger.LogWarning("No snapshot found for proposal {P} — cannot rollback", proposal.Id);
        }

        Interlocked.Increment(ref _rolledBackCount);
        proposal.Status = ProposalStatus.RolledBack;
        return true;
    }

    private DeploymentSnapshot CaptureSnapshot(string proposalId)
    {
        var frontier = _router.GetFrontier().Select(p => p with { }).ToList();
        var geneFitness = _genePool.AllGenes.Take(50)
            .Select(g => (g.Id, g.Fitness))
            .ToList();

        return new DeploymentSnapshot
        {
            ProposalId = proposalId,
            FrontierBefore = frontier,
            GeneFitnessBefore = geneFitness,
            PhaseBefore = _teacher.Phase,
            CapturedAt = DateTime.UtcNow
        };
    }

    private void CollectMetrics()
    {
        lock (_metricLock)
        {
            _currentMetrics.Clear();

            var frontier = _router.GetFrontier();
            _currentMetrics.Add(new ArchitectureMetric
            {
                Key = "frontier_size",
                Value = frontier.Count,
                Threshold = 3,
                Status = frontier.Count < 3 ? "critical" : frontier.Count < 5 ? "warning" : "ok"
            });

            var teacherStats = _teacher.GetStats();
            _currentMetrics.Add(new ArchitectureMetric
            {
                Key = "bootstrap_phase",
                Value = teacherStats.Phase == BootstrapPhase.Teaching ? 0 :
                        teacherStats.Phase == BootstrapPhase.Shadowing ? 1 : 2,
                Threshold = 2,
                Status = teacherStats.Phase == BootstrapPhase.Autonomous ? "ok" :
                         teacherStats.Phase == BootstrapPhase.Shadowing ? "warning" : "critical"
            });

            var geneHistory = _genePool.History;
            var lastGen = geneHistory.LastOrDefault();
            _currentMetrics.Add(new ArchitectureMetric
            {
                Key = "gene_population",
                Value = lastGen?.Born ?? 0,
                Threshold = 20,
                Status = (lastGen?.Born ?? 0) < 20 ? "warning" : "ok"
            });

            if (lastGen != null)
            {
                _currentMetrics.Add(new ArchitectureMetric
                {
                    Key = "gene_avg_fitness",
                    Value = lastGen.AvgFitness,
                    Threshold = 0.5,
                    Status = lastGen.AvgFitness < 0.3 ? "critical" : lastGen.AvgFitness < 0.5 ? "warning" : "ok"
                });
            }

            var annealHistory = _annealer.History;
            if (annealHistory.Count > 0)
            {
                var lastStep = annealHistory.Last();
                _currentMetrics.Add(new ArchitectureMetric
                {
                    Key = "annealer_accept_rate",
                    Value = lastStep.ProposalsGenerated > 0
                        ? (double)lastStep.ProposalsAccepted / lastStep.ProposalsGenerated : 0,
                    Threshold = 0.2,
                    Status = lastStep.ProposalsGenerated > 0 && lastStep.ProposalsAccepted == 0
                        ? "critical" : "ok"
                });
            }

            if (_semanticAnchor != null)
            {
                var (pt, ag) = _semanticAnchor.GetAnchorParams();
                _currentMetrics.Add(new ArchitectureMetric
                {
                    Key = "semantic_anchor_phase_threshold",
                    Value = pt,
                    Threshold = 0.75,
                    Status = pt > 1.5 ? "critical" : pt < 0.1 ? "critical" : "ok"
                });
                _currentMetrics.Add(new ArchitectureMetric
                {
                    Key = "semantic_anchor_gamma",
                    Value = ag,
                    Threshold = 0.1,
                    Status = ag > 0.5 ? "warning" : ag < 0.001 ? "warning" : "ok"
                });
                _currentMetrics.Add(new ArchitectureMetric
                {
                    Key = "semantic_anchor_anchored",
                    Value = _semanticAnchor.AnchoredLabels,
                    Threshold = 3,
                    Status = _semanticAnchor.AnchoredLabels == 0 ? "critical" : "ok"
                });
            }
        }
    }

    private string BuildDiagnosisPrompt()
    {
        var critical = _currentMetrics.Where(m => m.Status == "critical").ToList();
        var warnings = _currentMetrics.Where(m => m.Status == "warning").ToList();

        if (critical.Count == 0 && warnings.Count == 0) return "";

        var metricsJson = JsonSerializer.Serialize(_currentMetrics);
        var teacherStats = _teacher.GetStats();
        var teacherInfo = $"Phase={teacherStats.Phase}, Accuracy={teacherStats.CurrentAccuracy:F3}";
        var lastGen = _genePool.History.LastOrDefault();

        return $$""""
            [ROLE] You are the LTAI Architecture Diagnostician. Analyze system metrics and identify the ROOT CAUSE of any issues.

            [SYSTEM STATE]
            - Bootstrapping: {{teacherInfo}}
            - GenePool: {{JsonSerializer.Serialize(lastGen)}}

            [METRICS]
            {{metricsJson}}

            [CRITICAL]
            {{string.Join("\n", critical.Select(m => $"- {m.Key}: {m.Value:F2} (threshold {m.Threshold})"))}}

            [WARNINGS]
            {{string.Join("\n", warnings.Select(m => $"- {m.Key}: {m.Value:F2} (threshold {m.Threshold})"))}}

            [INSTRUCTIONS]
            Identify the single most impactful root cause. Output JSON:
            {"issue":"...","root_cause":"...","severity":0.5,"affected_components":["component1","component2"]}

            If no real issues, output: {"issue":"none"}
            """";
    }

    private string BuildProposalPrompt(ArchitectureDiagnosis diagnosis)
    {
        var availableActions = Enum.GetNames<ArchitectureAction>();
        var frontierInfo = JsonSerializer.Serialize(_router.GetFrontier());
        var anchorInfo = _semanticAnchor != null
            ? $"PhaseThreshold={_semanticAnchor.GetAnchorParams().PhaseThreshold:F2}, Gamma={_semanticAnchor.GetAnchorParams().AdaptiveGamma:F2}, Anchored={_semanticAnchor.AnchoredLabels}"
            : "N/A";

        return $$""""
            [ROLE] You are the LTAI Architecture Architect. Propose ONE concrete action to address the diagnosis.

            [DIAGNOSIS]
            - Issue: {{diagnosis.Issue}}
            - Root Cause: {{diagnosis.RootCause}}
            - Severity: {{diagnosis.Severity:F2}}
            - Affected: {{string.Join(", ", diagnosis.AffectedComponents)}}

            [CURRENT STATE]
            - Pareto Frontier: {{frontierInfo}}
            - Bootstrap Phase: {{_teacher.GetStats().Phase}}, Accuracy: {{_teacher.GetStats().CurrentAccuracy:F3}}
            - SemanticAnchor: {{anchorInfo}}

            [AVAILABLE ACTIONS]
            {{string.Join(", ", availableActions)}}

            [INSTRUCTIONS]
            Propose ONE action. Risk must be 0-1 (<0.3=low, <0.7=medium). Output JSON:
            {"action":"MutateGene","target_component":"GenePool","description":"...","expected_improvement":0.15,"risk":0.3,"parameters":{"generations":2},"rollback_strategy":"..."}
            """";
    }

    private static ArchitectureDiagnosis? ParseDiagnosis(string response)
    {
        try
        {
            var startIdx = response.IndexOf('{');
            var endIdx = response.LastIndexOf('}');
            if (startIdx < 0 || endIdx < 0) return null;
            var json = response[startIdx..(endIdx + 1)];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var issue = root.TryGetProperty("issue", out var i) ? i.GetString() ?? "" : "";
            if (issue == "none" || string.IsNullOrEmpty(issue)) return null;

            return new ArchitectureDiagnosis
            {
                Issue = issue,
                RootCause = root.TryGetProperty("root_cause", out var rc) ? rc.GetString() ?? "" : "",
                Severity = root.TryGetProperty("severity", out var s) && s.TryGetDouble(out var sd) ? sd : 0.5,
                AffectedComponents = root.TryGetProperty("affected_components", out var ac)
                    ? ac.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : new List<string>()
            };
        }
        catch
        {
            return null;
        }
    }

    private static ArchitectureProposal? ParseProposal(string response, string diagnosisId)
    {
        try
        {
            var startIdx = response.IndexOf('{');
            var endIdx = response.LastIndexOf('}');
            if (startIdx < 0 || endIdx < 0) return null;
            var json = response[startIdx..(endIdx + 1)];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var actionStr = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            if (!Enum.TryParse<ArchitectureAction>(actionStr, out var action)) return null;

            var parameters = new Dictionary<string, object>();
            if (root.TryGetProperty("parameters", out var pm))
            {
                foreach (var prop in pm.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                        parameters[prop.Name] = prop.Value.GetDouble();
                    else
                        parameters[prop.Name] = prop.Value.GetString() ?? "";
                }
            }

            return new ArchitectureProposal
            {
                DiagnosisId = diagnosisId,
                Description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                Action = action,
                TargetComponent = root.TryGetProperty("target_component", out var tc) ? tc.GetString() ?? "" : "",
                Parameters = parameters,
                ExpectedImprovement = root.TryGetProperty("expected_improvement", out var ei) && ei.TryGetDouble(out var eid) ? eid : 0.1,
                Risk = root.TryGetProperty("risk", out var r) && r.TryGetDouble(out var rd) ? rd : 0.5,
                RollbackStrategy = root.TryGetProperty("rollback_strategy", out var rs) ? rs.GetString() ?? "revert_to_snapshot" : "revert_to_snapshot"
            };
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<ArchitectureDiagnosis> GetDiagnosisHistory() => _diagnosisHistory.ToList();

    public IReadOnlyList<IAgent> GetDeployedAgents() => _deployedAgents.Values.ToList().AsReadOnly();

    private void PublishAgentEvent(string eventType, string agentId, string niche)
    {
        try
        {
            if (_serviceProvider?.GetService(typeof(CoordinationScheduler)) is CoordinationScheduler scheduler)
            {
                scheduler.PublishDynamic(eventType, "ArchitectLoop",
                    $"agent_id={agentId} niche={niche}");
            }
        }
        catch { }
    }

    private void ApplyProposalToShadow(ArchitectureProposal proposal, ParetoRouter shadow)
    {
        switch (proposal.Action)
        {
            case ArchitectureAction.AddParetoPoint:
                if (proposal.Parameters.TryGetValue("quality", out var q) &&
                    proposal.Parameters.TryGetValue("speed", out var s) &&
                    proposal.Parameters.TryGetValue("cost", out var c) &&
                    proposal.Parameters.TryGetValue("label", out var label))
                {
                    shadow.AddFrontierPoint(new ParetoPoint
                    {
                        Label = label?.ToString() ?? "",
                        Quality = Convert.ToSingle(q),
                        Speed = Convert.ToSingle(s),
                        Cost = Convert.ToSingle(c)
                    });
                }
                break;

            case ArchitectureAction.RemoveParetoPoint:
                shadow.PruneDominated();
                break;
        }
    }
    public IReadOnlyList<ArchitectureProposal> GetProposalHistory() => _proposalHistory.ToList();
    public IReadOnlyList<ArchitectureMetric> GetCurrentMetrics()
    {
        lock (_metricLock) return _currentMetrics.ToList();
    }
}
