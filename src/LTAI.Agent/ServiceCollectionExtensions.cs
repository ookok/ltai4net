using LTAI.Agent.Memory;
using LTAI.Agent.Vector;
using LTAI.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

/// <summary>
/// Top-level DI registration for the LTAI Agent subsystem.
///
/// Registration is split across 7 partial-class files under DI/ for maintainability.
/// Execution order (do not change — consumers depend on it):
///   1. Core (AgentToolStore, AgentRegistry, PromptLoader, agent definitions, durable agents)
///   2. Graph infra (KgStore, GloVe, lookup router, contracts, KbGraph, CgGraph)
///   3. MoE Experts (7 expert modules, router, fan-out, aggregator)
///   4. Pipeline (PipelineRunner, 12+ IPipelineStep, ContextOffloader, MermaidStateTracker)
///   5. Workflow (AgentWorkflows, DecisionTreeRouter, YAML hot-reload, MoA, steer, budget)
///   6. Memory & Persistence (PalaceStore, fallback, consolidation, compression store)
///   7. Indexing & Tools (DocumentIndexer, CodeChunkIndex, SkillEvolutionEngine)
///   8. ChatAgent (L1→L2 router, escalation decider)
///
/// Tool selection, prompt building, and context-provider assembly live in
/// <see cref="AgentBuilder"/>, <see cref="AgentPromptBuilder"/>, and
/// <see cref="AgentContextProviderBuilder"/> respectively.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
        => AddLTAIAgent(services, out _);

    /// <summary>
    /// Variant that also returns the list of registered agent names so callers (e.g. LTAI.Web)
    /// can wire up protocol endpoints (A2A / AGUI / OpenAI) without having to resolve every
    /// agent eagerly just to discover their names.
    /// </summary>
    public static IServiceCollection AddLTAIAgent(this IServiceCollection services, out IReadOnlyList<string> registeredAgentNames)
    {
        services.AddLTAIAgentCore(out registeredAgentNames);
        services.AddLTAIAgentSharedInfra();
        services.AddLTAIAgentGraphInfra();
        services.AddLTAIAgentExperts();
        services.AddLTAIAgentPipeline();
        services.AddLTAIAgentWorkflows();
        services.AddLTAIAgentMemory();
        services.AddLTAIAgentIndexingAndTools();
        services.AddLTAIAgentChat();

        return services;
    }

    /// <summary>Resolve the skills directory across fallback paths.</summary>
    internal static string ResolveSkillsDir()
    {
        return new[] {
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "skills"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "skills"),
        }.FirstOrDefault(Directory.Exists) ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");
    }

    /// <summary>
    /// Register the multi-graph memory system (MAGMA-inspired).
    /// Adds MultiGraphStore, AdaptiveBeamTraverser, IntentRouter,
    /// SalienceBudgetCompressor, and the slow-path consolidation worker.
    /// </summary>
    public static IServiceCollection AddLTAIMemory(this IServiceCollection services,
        Action<MultiGraphConfig>? configure = null)
    {
        var config = new MultiGraphConfig();
        configure?.Invoke(config);

        services.AddSingleton<MultiGraphStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var dbPath = opts.ResolveDataPath("kg.db");
            return new MultiGraphStore(dbPath, config.CausalThreshold, config.SemanticThreshold);
        });

        services.AddSingleton<AdaptiveBeamTraverser>();
        services.AddSingleton<IntentRouter>();
        services.AddSingleton<QueryClassifier>();
        services.AddSingleton<TriggerMatcher>();
        services.AddSingleton<SalienceBudgetCompressor>();

        return services;
    }
}

public sealed class MultiGraphConfig
{
    public double CausalThreshold { get; set; } = 0.7;
    public double SemanticThreshold { get; set; } = 0.6;
    public TimeSpan ConsolidationInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int ConsolidationBatchSize { get; set; } = 50;
}
