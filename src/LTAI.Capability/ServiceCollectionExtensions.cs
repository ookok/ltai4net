using LTAI.Capability.CodeEngine;
using LTAI.Capability.CodeGraph;
using LTAI.Capability.Crawler;
using LTAI.Capability.DocEngine;
using LTAI.Capability.Documents;
using LTAI.Capability.Evolution;
using LTAI.Capability.GIS;
using LTAI.Capability.Integration;
using LTAI.Capability.Knowledge;
using LTAI.Capability.Pipeline;
using LTAI.Capability.Reasoning;
using LTAI.Capability.Review;
using LTAI.Capability.Search;
using LTAI.Capability.Skills;
using LTAI.Capability.Tools;
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
        services.AddSingleton<CodeReviewEngine>();

        services.AddSingleton<TelegramBot>();
        services.AddSingleton<WechatWorkNotifier>();
        services.AddSingleton<AutoUpdater>();
        services.AddSingleton<UnifiedNotifier>();

        services.AddSingleton<SkillDiscoveryManager>();
        services.AddSingleton<SkillFactory>();
        services.AddSingleton<SkillCatalog>();

        services.AddSingleton<ToolMarket>();
        services.AddSingleton<ToolSynthesizer>();
        services.AddSingleton<ToolOrchestrator>();
        services.AddSingleton<ToolMeta>();

        services.AddSingleton<PipelineEngine>();

        services.AddSingleton<CodeGraph.CodeGraph>();

        services.AddSingleton<LightCrawler>();

        services.AddSingleton<SelfModifier>();
        services.AddSingleton<SelfDiscovery>();
        services.AddSingleton<SelfDocumenter>();

        services.AddSingleton<DocEngine.DocEngine>();
        services.AddSingleton<DocForge>();
        services.AddSingleton<DocumentPipeline>();
        services.AddSingleton<TemplateRegistry>();

        services.AddSingleton<KnowledgeForager>();

        return services;
    }
}
