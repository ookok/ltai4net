using LTAI.AI.Governors;
using LTAI.AI.Governors.Pipeline;
using LTAI.AI.Interfaces;
using LTAI.AI.Providers;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        // Core AI services — local model loading, speculative decoding, CellAI etc. removed.
        // Only cloud API-based model routing and basic infrastructure retained.

        // Pipeline governors
        services.AddSingleton<InputGovernor>();
        services.AddSingleton<ContextGovernor>();
        services.AddSingleton<RoutingGovernor>();
        services.AddSingleton<OutputGovernor>();
        services.AddSingleton<SelfGovernor>();

        // ReAct orchestrator
        services.AddSingleton<ReActLoopOrchestrator>();

        // LivingTree system
        services.AddSingleton<ILivingTreeSystem, LivingTreeSystem>();

        // Adaptive depth
        services.AddSingleton<AdaptiveDepthController>();

        // Cross-level distillation
        services.AddSingleton<CrossLevelDistiller>();

        // Tiered LoRA
        services.AddSingleton<TieredLoraManager>(sp =>
        {
            var depth = sp.GetRequiredService<AdaptiveDepthController>();
            var logger = sp.GetRequiredService<ILogger<TieredLoraManager>>();
            var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
            return new TieredLoraManager(modelsDir, depth, logger);
        });

        // Pipeline services
        services.AddSingleton<QueryPreprocessingService>();
        services.AddSingleton<GroundingVerificationService>();

        // Self-correction LoRA
        services.AddSingleton<SelfCorrectionLoRA>();

        // SPIN self-play
        services.AddSingleton<SpinSelfPlayLoop>();

        // Structure-aware router
        services.AddSingleton<StructureAwareRouter>();

        // Capability migrator
        services.AddSingleton<CapabilityMigrator>();

        // ContextHub — unified cross-domain memory retrieval (wired here but stores
        // are optional; ContextHub.Query() returns what's available at runtime).
        services.AddSingleton<ContextHub>(sp =>
        {
            var logger = sp.GetService<ILogger<ContextHub>>();
            return ContextHubBuilder.Build(
                dualMemory: sp.GetService<DualMemoryStore>(),
                memoryFiles: sp.GetService<MemoryFilesService>(),
                knowledgeGraph: sp.GetService<KnowledgeGraph>(),
                evolutionStore: sp.GetService<ICrossRunEvolutionStore>(),
                harnessEvo: sp.GetService<HarnessEvolution>(),
                contextMap: sp.GetService<ContextMapStore>(),
                synapticMemory: sp.GetService<SynapticMemory>(),
                contextGovernor: sp.GetService<ContextGovernor>(),
                dualRouteRetriever: sp.GetService<DualRouteRetriever>(),
                logger: logger
            );
        });
        services.AddSingleton<IContextHub>(sp => sp.GetRequiredService<ContextHub>());

        return services;
    }
}
