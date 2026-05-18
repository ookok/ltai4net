using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Multimodal;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMultimodal(this IServiceCollection services)
    {
        services.AddSingleton<OCREngine>();
        services.AddSingleton<VisionAnalyzer>();
        services.AddSingleton<SpeechEngine>();
        services.AddSingleton<MultimodalOrchestrator>();
        return services;
    }
}
