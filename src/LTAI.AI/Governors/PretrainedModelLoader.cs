using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class PretrainedModelLoader : IHostedService
{
    private readonly CellAIRegistry _cellRegistry;
    private readonly ILogger<PretrainedModelLoader> _logger;

    public PretrainedModelLoader(
        CellAIRegistry cellRegistry,
        ILogger<PretrainedModelLoader>? logger = null)
    {
        _cellRegistry = cellRegistry;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PretrainedModelLoader>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting pretrained model loader...");

        try
        {
            await _cellRegistry.InitializePretrainedModelsAsync(
                autoDownload: true,
                ct: cancellationToken).ConfigureAwait(false);

            var metrics = _cellRegistry.GetMetrics();
            _logger.LogInformation(
                "Pretrained models loaded: {Pretrained} pretrained, {SelfTrained} self-trained",
                metrics["pretrained_models"], metrics["self_trained_models"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pretrained models");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pretrained model loader stopped");
        return Task.CompletedTask;
    }
}
