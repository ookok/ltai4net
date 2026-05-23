using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LTAI.Infra.Multimodal;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMultimodal(this IServiceCollection services)
    {
        services.AddSingleton<OCREngine>();
        services.AddSingleton<RapidOCREngine>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<RapidOCREngine>>();
            var modelDir = global::System.IO.Path.Combine(AppContext.BaseDirectory, "models", "rapidocr");
            return new RapidOCREngine(modelDir, logger);
        });
        services.AddSingleton<VisionAnalyzer>();
        services.AddSingleton<SpeechEngine>();
        services.AddSingleton<MultimodalOrchestrator>();
        services.AddHostedService<OCREngineCleanupService>();
        return services;
    }

    internal sealed class OCREngineCleanupService(OCREngine ocr) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) { ocr.Dispose(); return Task.CompletedTask; }
    }
}
