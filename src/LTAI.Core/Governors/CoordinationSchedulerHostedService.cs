using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed class CoordinationSchedulerHostedService : BackgroundService
{
    private readonly CoordinationScheduler _scheduler;
    private readonly BootstrapTeacher _teacher;
    private readonly GenePool _genePool;
    private readonly SimulatedAnnealer _annealer;
    private readonly ArchitectLoop _architect;
    private readonly ILogger<CoordinationSchedulerHostedService> _logger;

    public CoordinationSchedulerHostedService(
        CoordinationScheduler scheduler,
        BootstrapTeacher teacher,
        GenePool genePool,
        SimulatedAnnealer annealer,
        ArchitectLoop architect,
        ILogger<CoordinationSchedulerHostedService>? logger = null)
    {
        _scheduler = scheduler;
        _teacher = teacher;
        _genePool = genePool;
        _annealer = annealer;
        _architect = architect;
        _logger = logger ?? NullLogger<CoordinationSchedulerHostedService>.Instance;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting CoordinationScheduler dispatch loop");

        _scheduler.RegisterBootstrapRules(_teacher, _genePool, _annealer, _architect);
        _scheduler.Start();

        stoppingToken.Register(() =>
        {
            _logger.LogInformation("Stopping CoordinationScheduler");
            _scheduler.StopAsync().GetAwaiter().GetResult();
        });

        return Task.CompletedTask;
    }
}
