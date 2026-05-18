using LTAI.Vector.Embedding;
using LTAI.Vector.Interfaces;
using LTAI.Vector.Knowledge;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Vector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIVector(this IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingBackend, LocalEmbeddingBackend>();
        services.AddSingleton<IVectorStore, VectorStore>();
        services.AddSingleton<DocumentStore>(sp =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "document_store.db");
            return new DocumentStore(dbPath,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DocumentStore>>());
        });
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
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<APIEmbeddingBackend>>()));
        services.AddSingleton<IVectorStore, VectorStore>();
        services.AddSingleton<DocumentStore>(sp =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "document_store.db");
            return new DocumentStore(dbPath,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DocumentStore>>());
        });
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
