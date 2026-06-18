using LTAI.Agent.Caching;
using LTAI.Agent.Context;
using LTAI.Agent.Memory;
using LTAI.Agent.Tools;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Memory and persistence: PalaceStore, FallbackRetriever, PalaceFeedbackTracker,
    /// MemoryConsolidationService, SessionMemoryExtractor, CompressionStore, SnippetStore.
    /// </summary>
    static IServiceCollection AddLTAIAgentMemory(this IServiceCollection services)
    {
        services.AddSingleton<FallbackRetriever>(sp =>
        {
            var store = sp.GetRequiredService<PalaceStore>();
            return new FallbackRetriever(store, topK: 5);
        });

        services.AddSingleton<PalaceFeedbackTracker>(sp =>
        {
            var store = sp.GetRequiredService<PalaceStore>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<PalaceFeedbackTracker>();
            return new PalaceFeedbackTracker(store, logger);
        });

        services.AddHostedService<SessionMemoryExtractor>(sp =>
        {
            var store = sp.GetRequiredService<PalaceStore>();
            var queue = sp.GetRequiredService<Tasks.TaskQueue>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<SessionMemoryExtractor>();
            return new SessionMemoryExtractor(store, queue, logger);
        });

        // PalaceStore must be registered AFTER ChatAgent because ChatAgent
        // lambda captures PalaceStore via DI (lazy resolution).
        services.AddSingleton<PalaceStore>(sp =>
        {
            var embedder = sp.GetRequiredService<LTAI.AI.EmbeddingClient>();
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PalaceStore>();
            return PalaceStore.CreateShared(embedder, opts.ResolveDataPath("kg.db"), logger);
        });

        services.AddHostedService<MemoryConsolidationService>(sp =>
        {
            var store = sp.GetRequiredService<PalaceStore>();
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryConsolidationService>();
            return new MemoryConsolidationService(store, l3, logger);
        });

        // CompressionStore shares kg.db
        services.AddSingleton<CompressionStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return CompressionStore.CreateShared(opts.ResolveDataPath("kg.db"));
        });

        services.AddSingleton<RetrieveContentTool>();
        services.AddSingleton<Delegation.DelegationContext>();

        // SnippetStore for user-defined phrases
        services.AddSingleton<Snippets.SnippetStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new Snippets.SnippetStore(
                opts.ResolveDataPath("snippets.json"),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Snippets.SnippetStore>());
        });

        // Conversation state checkpoint cache
        services.AddSingleton<IMemoryCachingStore>(sp =>
            new CachingCascade());

        return services;
    }
}
