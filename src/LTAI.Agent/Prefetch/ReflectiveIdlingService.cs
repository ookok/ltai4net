using LTAI.Agent.Agents;
using LTAI.Tools.Evolution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Prefetch;

public sealed class ReflectiveIdlingService : BackgroundService
{
    private readonly ILogger<ReflectiveIdlingService> _logger;
    private readonly ToolEvolutionLoop? _evolution;
    private const int LowLoadQpsThreshold = 5;
    private static readonly TimeSpan NightWindowStart = new(1, 0, 0);
    private static readonly TimeSpan NightWindowEnd = new(5, 0, 0);

    public ReflectiveIdlingService(
        ILogger<ReflectiveIdlingService> logger,
        ToolEvolutionLoop? evolution = null)
    {
        _logger = logger;
        _evolution = evolution;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("ReflectiveIdling: started (night window {Start}-{End})",
            NightWindowStart, NightWindowEnd);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow.TimeOfDay;
            var isNightWindow = now >= NightWindowStart && now <= NightWindowEnd;

            if (isNightWindow)
            {
                _logger.LogInformation("ReflectiveIdling: entering night reflection cycle");

                if (_evolution != null)
                {
                    await _evolution.DryRunCycleAsync(ct);
                    _logger.LogInformation("ReflectiveIdling: DryRun evolution scan complete");
                }

                _logger.LogInformation("ReflectiveIdling: night cycle complete, sleeping 1 hour");
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
        }
    }
}
