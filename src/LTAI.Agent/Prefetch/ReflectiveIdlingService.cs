using LTAI.Agent.Agents;
using LTAI.Tools.Evolution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Prefetch;

/// <summary>
/// ReflectiveIdlingService: idle-time background processing.
/// ProAct integration (arXiv:2605.25971): during idle windows,
/// predicts upcoming user needs and pre-computes responses.
/// </summary>
public sealed class ReflectiveIdlingService : BackgroundService
{
    private readonly ILogger<ReflectiveIdlingService> _logger;
    private readonly ToolEvolutionLoop? _evolution;
    private readonly ProActAnticipator? _proAct;
    private const int LowLoadQpsThreshold = 5;
    private static readonly TimeSpan NightWindowStart = new(1, 0, 0);
    private static readonly TimeSpan NightWindowEnd = new(5, 0, 0);

    public ReflectiveIdlingService(
        ILogger<ReflectiveIdlingService> logger,
        ToolEvolutionLoop? evolution = null,
        ProActAnticipator? proAct = null)
    {
        _logger = logger;
        _evolution = evolution;
        _proAct = proAct;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("ReflectiveIdling: started (night window {Start}-{End}, ProAct={ProAct})",
            NightWindowStart, NightWindowEnd, _proAct != null);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow.TimeOfDay;
            var isNightWindow = now >= NightWindowStart && now <= NightWindowEnd;

            if (isNightWindow)
            {
                _logger.LogInformation("ReflectiveIdling: entering night reflection cycle");

                if (_evolution != null)
                {
                    await _evolution.DryRunCycleAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("ReflectiveIdling: DryRun evolution scan complete");
                }

                _logger.LogInformation("ReflectiveIdling: night cycle complete, sleeping 1 hour");
                await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
            }
            else
            {
                // ProAct: run anticipation cycle during idle time (every 30s)
                if (_proAct != null)
                {
                    try
                    {
                        await _proAct.RunAnticipationCycleAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ProAct anticipation cycle failed");
                    }
                }

                // Daytime idle: shorter sleep so ProAct can run frequently
                await Task.Delay(
                    _proAct != null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5),
                    ct).ConfigureAwait(false);
            }
        }
    }
}
