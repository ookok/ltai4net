using LTAI.Agent.Context;
using LTAI.Agent.Memory;
using LTAI.Agent.Orchestration;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Scheduling;
using LTAI.Agent.Vector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Pipeline steps, PipelineRunner, and supporting infrastructure.
    /// ContextOffloader, MermaidStateTracker, SolutionPool, ActiveContextCompressor.
    /// </summary>
    static IServiceCollection AddLTAIAgentPipeline(this IServiceCollection services)
    {
        services.AddSingleton<ContextOffloader>();
        services.AddSingleton<MermaidStateTracker>();

        // ── Pre-generation pipeline steps ──
        services.AddSingleton<ProgressGuardStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ProgressGuardStep>());
        services.AddSingleton<SafetyCheckStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<SafetyCheckStep>());
        services.AddSingleton<ToolExecutionStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ToolExecutionStep>());
        services.AddSingleton<MemoryCachingStep>(sp =>
            new MemoryCachingStep(
                sp.GetRequiredService<Caching.IMemoryCachingStore>(),
                afterRouter: false, // restore mode
                logger: sp.GetService<ILogger<MemoryCachingStep>>()));
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<MemoryCachingStep>());

        // ── Post-generation pipeline steps ──
        services.AddSingleton<CompactionStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<CompactionStep>());
        services.AddSingleton<GrammarCheckStep>(sp =>
            new GrammarCheckStep(
                logger: sp.GetService<ILogger<GrammarCheckStep>>(),
                tsParser: sp.GetService<CodeAnalysis.TreeSitterParser>(),
                lspManager: sp.GetService<LanguageServer.LspLanguageManager>()));
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<GrammarCheckStep>());
        services.AddSingleton<AntiPatternCheckStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<AntiPatternCheckStep>());
        services.AddSingleton<QualityGateStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<QualityGateStep>());
        services.AddSingleton<DoDCheckStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<DoDCheckStep>());
        services.AddSingleton<ThinkingTagStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ThinkingTagStep>());
        services.AddSingleton<DiscoursePlanningStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<DiscoursePlanningStep>());
        services.AddSingleton<RetrospectiveStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<RetrospectiveStep>());

        // PipelineRunner with all registered IPipelineStep instances
        services.AddSingleton<PipelineRunner>();
        // Second MemoryCachingStep for save mode (different Name → different IPipelineStep)
        services.AddSingleton<IPipelineStep>(sp =>
            new MemoryCachingStep(
                sp.GetRequiredService<Caching.IMemoryCachingStore>(),
                afterRouter: true, // save mode
                logger: sp.GetService<ILogger<MemoryCachingStep>>()));
        services.AddSingleton<SolutionPool>();
        services.AddHostedService<ActiveContextCompressor>();
        services.AddTransient<Func<HypothesisRouterContext>>(_ =>
            () => HypothesisRouterContext.Create().Add("default").Build());

        return services;
    }
}
