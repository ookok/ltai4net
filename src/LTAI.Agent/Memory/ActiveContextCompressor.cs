using LTAI.Agent.Context;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed class ActiveContextCompressor : BackgroundService
{
    private readonly CompactionStep _compactor;
    private readonly ILogger<ActiveContextCompressor> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private double _lastRatio;

    public ActiveContextCompressor(ILogger<ActiveContextCompressor>? logger = null)
    {
        _compactor = new CompactionStep(logger: null, ratioThreshold: 0.6);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ActiveContextCompressor>.Instance;
    }

    public double CurrentRatio => _lastRatio;

    public async Task<bool> TryCompressAsync(MessageContext context)
    {
        if (context == null) return false;
        var contextRatio = UsageTracker.ContextRatio();
        _lastRatio = contextRatio;

        if (contextRatio < 0.6)
        {
            _logger.LogDebug("ActiveContextCompressor: ratio {Pct:F0}% < 60%, skipping", contextRatio * 100);
            return false;
        }

        _logger.LogInformation("ActiveContextCompressor: ratio {Pct:F0}% >= 60%, compressing", contextRatio * 100);
        await _compactor.ProcessAsync(context).ConfigureAwait(false);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ActiveContextCompressor: started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested) break;

                var ratio = UsageTracker.ContextRatio();
                _lastRatio = ratio;

                if (ratio >= 0.6)
                {
                    _logger.LogInformation("ActiveContextCompressor: detected high ratio {Pct:F0}%, agent should compress", ratio * 100);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ActiveContextCompressor: check failed");
            }
        }
    }
}
