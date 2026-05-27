using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed class EvolutionLoopHostedService : BackgroundService
{
    private readonly ParetoRouter _router;
    private readonly BootstrapTeacher _teacher;
    private readonly GenePool _genePool;
    private readonly SimulatedAnnealer _annealer;
    private readonly GeneToRule _geneToRule;
    private readonly ArchitectLoop _architect;
    private readonly ILogger<EvolutionLoopHostedService> _logger;

    private readonly TimeSpan _evolutionInterval;
    private readonly TimeSpan _architectInterval;
    private readonly TimeSpan _deployInterval;

    public EvolutionLoopHostedService(
        ParetoRouter router,
        BootstrapTeacher teacher,
        GenePool genePool,
        SimulatedAnnealer annealer,
        GeneToRule geneToRule,
        ArchitectLoop architect,
        TimeSpan? evolutionInterval = null,
        TimeSpan? architectInterval = null,
        TimeSpan? deployInterval = null,
        ILogger<EvolutionLoopHostedService>? logger = null)
    {
        _router = router;
        _teacher = teacher;
        _genePool = genePool;
        _annealer = annealer;
        _geneToRule = geneToRule;
        _architect = architect;
        _evolutionInterval = evolutionInterval ?? TimeSpan.FromMinutes(2);
        _architectInterval = architectInterval ?? TimeSpan.FromMinutes(5);
        _deployInterval = deployInterval ?? TimeSpan.FromMinutes(10);
        _logger = logger ?? NullLogger<EvolutionLoopHostedService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Evolution Loop started: evolution={EvoMin}m, architect={ArchMin}m, deploy={DeployMin}m",
            _evolutionInterval.TotalMinutes, _architectInterval.TotalMinutes, _deployInterval.TotalMinutes);

        var evolutionClock = Task.Delay(_evolutionInterval, stoppingToken);
        var architectClock = Task.Delay(_architectInterval, stoppingToken);
        var deployClock = Task.Delay(_deployInterval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(evolutionClock, architectClock, deployClock).ConfigureAwait(false);

            if (completed == evolutionClock)
            {
                try
                {
                    var epoch = await _annealer.StepAsync(proposalsPerEpoch: 10, stoppingToken).ConfigureAwait(false);
                    _logger.LogDebug("Evolution: epoch={Epoch} temp={Temp:F3} accepted={Acc}/{Prop}",
                        epoch.Epoch, epoch.Temperature, epoch.ProposalsAccepted, epoch.ProposalsGenerated);

                    var gen = _genePool.Evolve(eliteCount: 3, crossoverCount: 5, mutateCount: 7);
                    _logger.LogDebug("Evolve: gen={Gen} pop={Pop} avgF={Avg:F3}",
                        gen.Generation, gen.PopulationSize, gen.AvgFitness);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Evolution step failed");
                }
                evolutionClock = Task.Delay(_evolutionInterval, stoppingToken);
            }

            if (completed == architectClock)
            {
                try
                {
                    var proposal = await _architect.RunAsync(stoppingToken).ConfigureAwait(false);
                    if (proposal != null && proposal.Status == ProposalStatus.Deployed)
                    {
                        _logger.LogInformation("Architect deployed: {Action} → {Target}",
                            proposal.Action, proposal.TargetComponent);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Architect loop failed");
                }
                architectClock = Task.Delay(_architectInterval, stoppingToken);
            }

            if (completed == deployClock)
            {
                try
                {
                    var deployed = await _geneToRule.DeployTopGenesAsync(topN: 5, ct: stoppingToken).ConfigureAwait(false);
                    if (deployed > 0)
                        _logger.LogInformation("Deployed {Count} top genes to ParetoRouter", deployed);

                    _genePool.DecayUnused(TimeSpan.FromHours(1));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Deploy cycle failed");
                }
                deployClock = Task.Delay(_deployInterval, stoppingToken);
            }
        }

        _logger.LogInformation("Evolution Loop stopped");
    }

    public int GetEvolutionEpoch() => _annealer.Epoch;
    public int GetGeneGeneration() => _genePool.Generation;
    public BootstrapPhase GetBootstrapPhase() => _teacher.GetStats().Phase;
}
