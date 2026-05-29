using LTAI.Tools.CodeEngine;
using LTAI.Tools.CodeGraph;
using LTAI.Tools.DocEngine;
using LTAI.Tools.Evolution;
using LTAI.Tools.Integration;
using LTAI.Tools.Knowledge;
using LTAI.Tools.Pipeline;
using LTAI.Tools.Reasoning;
using LTAI.Tools.Review;
using LTAI.Tools.Search;
using LTAI.Tools.Skills;
using LTAI.Tools.Tools;
using LTAI.Tools.Capability.Governance;
using LTAI.Tools.Capability;
using LTAI.Core.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools;

public static class CapabilityServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICapability(this IServiceCollection services)
    {
        services.AddSingleton<ParserRegistry>();
        services.AddSingleton<MultiLangCodeAnalyzer>();
        services.AddSingleton<UnifiedSearchEngine>();
        services.AddSingleton<MathReasoner>();
        services.AddSingleton<FormalLogicEngine>();
        services.AddSingleton<DialecticalReasoner>();
        services.AddSingleton<AttributionReasoner>();
        services.AddSingleton<ReasoningOrchestrator>();

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
        services.AddSingleton<ToolDashboard>();
        services.AddSingleton<ToolEvolutionLoop>();
        services.AddHostedService(sp => sp.GetRequiredService<ToolEvolutionLoop>());

        services.AddSingleton<PipelineEngine>();

        services.AddSingleton<CodeGraphEnhanced>(sp =>
            new CodeGraphEnhanced(sp.GetRequiredService<DataPathResolver>()));

        services.AddSingleton<SelfModifier>();
        services.AddSingleton<SelfDiscovery>();
        services.AddSingleton<SelfDocumenter>();

        services.AddSingleton<CodeEditEngine>(sp =>
            new CodeEditEngine(
                parser: sp.GetRequiredService<ParserRegistry>().GetParser(CodeLanguage.CSharp),
                logger: sp.GetRequiredService<ILogger<CodeEditEngine>>()));
        services.AddSingleton<BuildPipeline>();
        services.AddSingleton<CSharpCompilationService>();
        services.AddSingleton<TestHarness>(sp =>
            new TestHarness(sp.GetService<CodeGraphEnhanced>(),
                sp.GetRequiredService<ILogger<TestHarness>>()));

        services.AddSingleton<DocEngine.DocEngine>();
        services.AddSingleton<DocForge>();
        services.AddSingleton<DocumentPipeline>();
        services.AddSingleton<TemplateRegistry>();

        services.AddSingleton<KnowledgeForager>();

        services.AddSingleton<MessageGateway>();
        services.AddSingleton<WXBizMsgCrypt>();
        services.AddSingleton<WeWorkBot>();
        services.AddSingleton<PkgManager>();

        return services;
    }
}
