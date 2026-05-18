using LTAI.Capability.CodeEngine;
using LTAI.Capability.Documents;
using LTAI.Capability.GIS;
using LTAI.Capability.Reasoning;
using LTAI.Capability.Search;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Capability;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICapability(this IServiceCollection services)
    {
        services.AddSingleton<MultiLangCodeAnalyzer>();
        services.AddSingleton<UnifiedSearchEngine>();
        services.AddSingleton<DocumentProcessor>();

        services.AddSingleton<MathReasoner>();
        services.AddSingleton<FormalLogicEngine>();
        services.AddSingleton<DialecticalReasoner>();
        services.AddSingleton<AttributionReasoner>();
        services.AddSingleton<ReasoningOrchestrator>();

        services.AddSingleton<UnifiedMapService>();

        return services;
    }
}
