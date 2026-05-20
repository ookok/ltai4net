using LTAI.Vector.Embedding;
using LTAI.Vector.Interfaces;
using LTAI.Vector.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;

namespace LTAI.Vector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIVector(this IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingBackend, LocalEmbeddingBackend>();

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new EmbeddingGeneratorAdapter(sp.GetRequiredService<IEmbeddingBackend>()));

        services.AddSingleton<IVectorStore, VectorStore>();
        services.AddSingleton<DocumentStore>(sp =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "document_store.db");
            return new DocumentStore(dbPath,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<ILogger<DocumentStore>>());
        });

        services.AddSingleton<IKernelMemory>(sp =>
        {
            var backend = sp.GetRequiredService<IEmbeddingBackend>();
            var kmAdapter = new KernelMemoryEmbeddingAdapter(backend);

            return new KernelMemoryBuilder()
                .WithCustomEmbeddingGenerator(kmAdapter)
                .WithSimpleVectorDb()
                .Build();
        });

        services.AddSingleton<KernelMemoryStore>();

        services.AddSingleton<HierarchicalChunker>();
        services.AddSingleton<MultiDocFusion>();
        services.AddSingleton<BasicContextWiki>();

        services.AddSingleton<KnowledgeBase>();
        services.AddSingleton<KnowledgeGraph>();
        services.AddSingleton<RelationEngine>();
        services.AddSingleton<TemporalCompressor>();
        services.AddSingleton<SignalCleaner>();
        services.AddSingleton<StructMemory>();
        services.AddSingleton<Reranker>();
        services.AddSingleton<QueryDecomposer>();
        services.AddSingleton<AgenticRAG>();
        return services;
    }

    public static IServiceCollection AddLTAIVectorWithAPI(
        this IServiceCollection services,
        string endpoint,
        string apiKey,
        string model = "text-embedding-3-small",
        int dimension = 1536)
    {
        services.AddSingleton<IEmbeddingBackend>(sp =>
            new APIEmbeddingBackend(endpoint, apiKey, model, dimension,
                sp.GetRequiredService<ILogger<APIEmbeddingBackend>>()));

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new EmbeddingGeneratorAdapter(sp.GetRequiredService<IEmbeddingBackend>()));

        services.AddSingleton<IVectorStore, VectorStore>();
        services.AddSingleton<DocumentStore>(sp =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "document_store.db");
            return new DocumentStore(dbPath,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<ILogger<DocumentStore>>());
        });

        services.AddSingleton<IKernelMemory>(sp =>
        {
            var backend = sp.GetRequiredService<IEmbeddingBackend>();
            var kmAdapter = new KernelMemoryEmbeddingAdapter(backend);

            return new KernelMemoryBuilder()
                .WithCustomEmbeddingGenerator(kmAdapter)
                .WithSimpleVectorDb()
                .Build();
        });

        services.AddSingleton<KernelMemoryStore>();

        services.AddSingleton<HierarchicalChunker>();
        services.AddSingleton<MultiDocFusion>();
        services.AddSingleton<BasicContextWiki>();

        services.AddSingleton<KnowledgeBase>();
        services.AddSingleton<KnowledgeGraph>();
        services.AddSingleton<RelationEngine>();
        services.AddSingleton<TemporalCompressor>();
        services.AddSingleton<SignalCleaner>();
        services.AddSingleton<StructMemory>();
        services.AddSingleton<Reranker>();
        services.AddSingleton<QueryDecomposer>();
        services.AddSingleton<AgenticRAG>();
        return services;
    }
}
