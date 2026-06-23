using LTAI.AI;
using LTAI.Agent.Experts;
using LTAI.Agent.Experts.Adapters;
using LTAI.Agent.Experts.Routing;
using LTAI.Agent.Memory;
using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// MoE Experts: QueryEmbeddingCache, 7× IExpertModule, ExpertRegistry,
    /// ExpertRouter, FanOut, Aggregator, Feedback, Entropy, MemoryCompressor, FactExtractor.
    /// </summary>
    static IServiceCollection AddLTAIAgentExperts(this IServiceCollection services)
    {
        services.AddSingleton<QueryEmbeddingCache>();

        services.AddSingleton<IExpertModule, KbGraphExpert>(sp =>
        {
            var kbGraph = sp.GetRequiredService<KbGraph>();
            var kgStore = sp.GetRequiredService<KgStore>();
            return new KbGraphExpert(kbGraph, kgStore);
        });
        services.AddSingleton<IExpertModule>(sp =>
            new ShardedCgGraphExpert(sp.GetRequiredService<CgGraph>()));
        services.AddSingleton<IExpertModule>(sp =>
            DocumentExpert.CreateApiDocExpert(sp.GetRequiredService<KbGraph>()));
        services.AddSingleton<IExpertModule>(sp =>
            DocumentExpert.CreateRunbookExpert(sp.GetRequiredService<KbGraph>()));
        services.AddSingleton<IExpertModule>(sp =>
            DocumentExpert.CreateDesignDocExpert(sp.GetRequiredService<KbGraph>()));
        services.AddSingleton<IExpertModule, ToolExpert>(sp =>
            new ToolExpert(
                sp.GetRequiredService<EmbeddingClient>(),
                sp.GetRequiredService<IToolRegistry>()));
        services.AddSingleton<IExpertModule, SkillExpert>(sp =>
        {
            var skillsDir = ResolveSkillsDir();
            Directory.CreateDirectory(skillsDir);
            return new SkillExpert(skillsDir);
        });

        services.AddSingleton<ExpertRegistry>(sp =>
        {
            var experts = sp.GetRequiredService<IEnumerable<IExpertModule>>();
            var embedder = sp.GetRequiredService<EmbeddingClient>();
            var cache = sp.GetService<ToolEmbeddingCache>();
            var queryCache = sp.GetService<QueryEmbeddingCache>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExpertRegistry>();
            return new ExpertRegistry(experts, embedder, cache, queryCache, logger);
        });

        services.AddSingleton<ExpertRouter>(sp =>
        {
            var registry = sp.GetRequiredService<ExpertRegistry>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExpertRouter>();
            return new ExpertRouter(registry, logger);
        });
        services.AddSingleton<ParallelFanOutExecutor>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ParallelFanOutExecutor>();
            return new ParallelFanOutExecutor(logger);
        });
        services.AddSingleton<ExpertAggregator>(sp =>
        {
            var embedder = sp.GetService<EmbeddingClient>();
            return new ExpertAggregator(embedder);
        });
        services.AddSingleton<ExpertFeedbackLogger>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExpertFeedbackLogger>();
            return new ExpertFeedbackLogger(logger);
        });
        services.AddSingleton<EntropyTracker>(sp =>
        {
            var feedback = sp.GetService<ExpertFeedbackLogger>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<EntropyTracker>();
            return new EntropyTracker(feedback, logger);
        });
        services.AddSingleton<MemoryCompressor>(sp =>
        {
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryCompressor>();
            return new MemoryCompressor(l3, logger);
        });
        services.AddSingleton<FactExtractor>(sp =>
        {
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<FactExtractor>();
            return new FactExtractor(l3, logger);
        });

        return services;
    }
}
