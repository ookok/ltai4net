using LTAI.Agent.Context;
using LTAI.Agent.Evolution;
using LTAI.Agent.Memory;
using LTAI.Agent.Orchestration;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Scheduling;
using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
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
        // ContextOffloader registered in ToolAndSkillRegistration with full DI args
        services.AddSingleton<MermaidStateTracker>();

        // ── Meta-Skill evolution ──
        services.AddSingleton<MetaSkillStore>();
        services.AddSingleton<ContrastiveReflectionService>();
        services.AddSingleton<RegressionTestSuite>();
        services.AddSingleton<SkillEvolutionOrchestrator>(sp =>
        {
            var store = sp.GetRequiredService<MetaSkillStore>();
            var cr = sp.GetRequiredService<ContrastiveReflectionService>();
            var planStore = sp.GetRequiredService<PlanLearningStore>();
            var palaceStore = sp.GetRequiredService<PalaceStore>();
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetService<ILogger<SkillEvolutionOrchestrator>>();
            var regression = sp.GetRequiredService<RegressionTestSuite>();
            return new SkillEvolutionOrchestrator(store, cr, planStore, palaceStore, regression, l3, logger);
        });
        services.AddHostedService<SkillEvolutionOrchestrator>(sp =>
            sp.GetRequiredService<SkillEvolutionOrchestrator>());

        // ── Pre-generation pipeline steps ──
        services.AddSingleton<MetaSkillInjectorStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<MetaSkillInjectorStep>());
        services.AddSingleton<MultiTrajectoryRolloutStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<MultiTrajectoryRolloutStep>());
        services.AddSingleton<ProgressGuardStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ProgressGuardStep>());
        services.AddSingleton<DecompositionStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<DecompositionStep>());
        services.AddSingleton<SADFeedbackStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<SADFeedbackStep>());
        services.AddSingleton<CompositionStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<CompositionStep>());
        services.AddSingleton<SafetyCheckStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<SafetyCheckStep>());
        services.AddSingleton<ToolExecutionStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ToolExecutionStep>());

        // Pre-generation steps that were declared in the step plan but previously
        // unregistered (silently skipped). Their dependencies are either registered
        // or nullable, so registration is safe.
        services.AddSingleton<RagContextStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<RagContextStep>());
        services.AddSingleton<ReflectionAugmentedStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ReflectionAugmentedStep>());
        services.AddSingleton<ProactiveSuggestStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ProactiveSuggestStep>());
        services.AddSingleton<MemoryCachingStep>(sp =>
            new MemoryCachingStep(
                sp.GetRequiredService<Caching.IMemoryCachingStore>(),
                afterRouter: false, // restore mode
                logger: sp.GetService<ILogger<MemoryCachingStep>>()));
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<MemoryCachingStep>());

        // ── Post-generation pipeline steps ──
        services.AddSingleton<DeltaAnchorStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<DeltaAnchorStep>());
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
        services.AddSingleton<AntiPatternPatchStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<AntiPatternPatchStep>());
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
        services.AddSingleton<AbstentionCheckStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<AbstentionCheckStep>());
        services.AddSingleton<ToolEvalStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<ToolEvalStep>());
        services.AddSingleton<SelfRefineStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<SelfRefineStep>());
        services.AddSingleton<GenerationOrderStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<GenerationOrderStep>());
        services.AddSingleton<SelfReflectionStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<SelfReflectionStep>());
        services.AddSingleton<CriticRepairStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<CriticRepairStep>());

        // ── Plan verification + dynamic replan ──
        services.AddSingleton<PlanVerificationStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<PlanVerificationStep>());
        services.AddSingleton<DynamicReplanStep>();
        services.AddSingleton<IPipelineStep>(sp => sp.GetRequiredService<DynamicReplanStep>());

        // ── Cross-session plan learning ──
        services.AddSingleton<PlanLearningStore>();

        // ── Mandol-inspired services ──
        services.AddSingleton<QueryAwareMemoryRouter>();
        services.AddSingleton<SubgraphExpansionService>();

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
