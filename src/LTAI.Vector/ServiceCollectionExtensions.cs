using LTAI.Core.System;
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
            var dbPath = sp.GetRequiredService<DataPathResolver>().GetPath("document_store.db");
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

        services.AddSingleton<Bm25Scorer>();
        services.AddSingleton<CompiledTruthStore>(sp =>
        {
            var dbPath = sp.GetRequiredService<DataPathResolver>().GetPath("brain.db");
            return new CompiledTruthStore(dbPath);
        });
        services.AddSingleton<UnifiedBrainStore>(sp =>
        {
            var dbPath = sp.GetRequiredService<DataPathResolver>().GetPath("brain.db");
            return new UnifiedBrainStore(dbPath,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<ILogger<UnifiedBrainStore>>());
        });

        services.AddSingleton<NeocorticalConsolidator>(sp =>
        {
            var structMemory = sp.GetRequiredService<StructMemory>();
            var logger = sp.GetService<ILogger<NeocorticalConsolidator>>();
            return new NeocorticalConsolidator(structMemory, logger);
        });

        services.AddSingleton<MemoryPoisoningDefense>();

        services.AddSingleton<CompositionalGeneralizer>(sp =>
        {
            var graph = sp.GetRequiredService<KnowledgeGraph>();
            var relEngine = sp.GetRequiredService<RelationEngine>();
            var logger = sp.GetService<ILogger<CompositionalGeneralizer>>();
            return new CompositionalGeneralizer(graph, relEngine, logger);
        });

        services.AddSingleton<ShardedMemoryStore>(sp =>
            new ShardedMemoryStore(totalShards: Environment.ProcessorCount * 2));

        services.AddSingleton<EagerPurgingCleaner>();

        services.AddSingleton<AgentHeapManager>();

        services.AddSingleton<RetrievalLatencySla>();

        services.AddSingleton<ArtifactStore>();
        services.AddSingleton<ProvenanceTracker>();
        services.AddSingleton<KnowledgeCompiler>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var artifactStore = sp.GetRequiredService<ArtifactStore>();
            var provenanceTracker = sp.GetRequiredService<ProvenanceTracker>();
            var vectorStore = sp.GetRequiredService<IVectorStore>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var logger = sp.GetService<ILogger<KnowledgeCompiler>>();
            return new KnowledgeCompiler(chatClient, artifactStore,
                provenanceTracker, vectorStore, agenticRAG, logger);
        });
        services.AddSingleton<KnowQLQueryService>(sp =>
        {
            var artifactStore = sp.GetRequiredService<ArtifactStore>();
            var provenanceTracker = sp.GetRequiredService<ProvenanceTracker>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var kg = sp.GetRequiredService<KnowledgeGraph>();
            var logger = sp.GetService<ILogger<KnowQLQueryService>>();
            return new KnowQLQueryService(artifactStore,
                provenanceTracker, agenticRAG, kg, logger);
        });

        services.AddSingleton<MarkdownKnowledgeGraph>(sp =>
        {
            var root = AppContext.BaseDirectory;
            var embedding = sp.GetRequiredService<IEmbeddingBackend>();
            var vectorStore = sp.GetRequiredService<IVectorStore>();
            var kg = new MarkdownKnowledgeGraph(root, embedding, vectorStore);
            kg.Initialize();
            return kg;
        });

        services.AddSingleton<CodeLinkTracker>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            return new CodeLinkTracker(kg);
        });

        services.AddSingleton<LatCheckValidator>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            var tracker = sp.GetRequiredService<CodeLinkTracker>();
            return new LatCheckValidator(kg, tracker);
        });

        services.AddSingleton<TestSpecEnforcer>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            var tracker = sp.GetRequiredService<CodeLinkTracker>();
            return new TestSpecEnforcer(kg, tracker);
        });

        services.AddSingleton<LatAgentHook>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            var validator = sp.GetRequiredService<LatCheckValidator>();
            var tracker = sp.GetRequiredService<CodeLinkTracker>();
            var enforcer = sp.GetRequiredService<TestSpecEnforcer>();
            return new LatAgentHook(kg, validator, AppContext.BaseDirectory, tracker, enforcer);
        });

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
            var dbPath = sp.GetRequiredService<DataPathResolver>().GetPath("document_store.db");
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

        services.AddSingleton<Bm25Scorer>();
        services.AddSingleton<CompiledTruthStore>(sp =>
        {
            var dbPath = sp.GetRequiredService<DataPathResolver>().GetPath("brain.db");
            return new CompiledTruthStore(dbPath);
        });
        services.AddSingleton<UnifiedBrainStore>(sp =>
        {
            var dbPath = sp.GetRequiredService<DataPathResolver>().GetPath("brain.db");
            return new UnifiedBrainStore(dbPath,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<ILogger<UnifiedBrainStore>>());
        });

        services.AddSingleton<NeocorticalConsolidator>(sp =>
        {
            var structMemory = sp.GetRequiredService<StructMemory>();
            var logger = sp.GetService<ILogger<NeocorticalConsolidator>>();
            return new NeocorticalConsolidator(structMemory, logger);
        });

        services.AddSingleton<MemoryPoisoningDefense>();

        services.AddSingleton<CompositionalGeneralizer>(sp =>
        {
            var graph = sp.GetRequiredService<KnowledgeGraph>();
            var relEngine = sp.GetRequiredService<RelationEngine>();
            var logger = sp.GetService<ILogger<CompositionalGeneralizer>>();
            return new CompositionalGeneralizer(graph, relEngine, logger);
        });

        services.AddSingleton<ShardedMemoryStore>(sp =>
            new ShardedMemoryStore(totalShards: Environment.ProcessorCount * 2));

        services.AddSingleton<EagerPurgingCleaner>();

        services.AddSingleton<AgentHeapManager>();

        services.AddSingleton<RetrievalLatencySla>();

        services.AddSingleton<ArtifactStore>();
        services.AddSingleton<ProvenanceTracker>();
        services.AddSingleton<KnowledgeCompiler>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var artifactStore = sp.GetRequiredService<ArtifactStore>();
            var provenanceTracker = sp.GetRequiredService<ProvenanceTracker>();
            var vectorStore = sp.GetRequiredService<IVectorStore>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var logger = sp.GetService<ILogger<KnowledgeCompiler>>();
            return new KnowledgeCompiler(chatClient, artifactStore,
                provenanceTracker, vectorStore, agenticRAG, logger);
        });
        services.AddSingleton<KnowQLQueryService>(sp =>
        {
            var artifactStore = sp.GetRequiredService<ArtifactStore>();
            var provenanceTracker = sp.GetRequiredService<ProvenanceTracker>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var kg = sp.GetRequiredService<KnowledgeGraph>();
            var logger = sp.GetService<ILogger<KnowQLQueryService>>();
            return new KnowQLQueryService(artifactStore,
                provenanceTracker, agenticRAG, kg, logger);
        });

        services.AddSingleton<MarkdownKnowledgeGraph>(sp =>
        {
            var root = AppContext.BaseDirectory;
            var embedding = sp.GetRequiredService<IEmbeddingBackend>();
            var vectorStore = sp.GetRequiredService<IVectorStore>();
            var kg = new MarkdownKnowledgeGraph(root, embedding, vectorStore);
            kg.Initialize();
            return kg;
        });

        services.AddSingleton<CodeLinkTracker>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            return new CodeLinkTracker(kg);
        });

        services.AddSingleton<LatCheckValidator>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            var tracker = sp.GetRequiredService<CodeLinkTracker>();
            return new LatCheckValidator(kg, tracker);
        });

        services.AddSingleton<TestSpecEnforcer>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            var tracker = sp.GetRequiredService<CodeLinkTracker>();
            return new TestSpecEnforcer(kg, tracker);
        });

        services.AddSingleton<LatAgentHook>(sp =>
        {
            var kg = sp.GetRequiredService<MarkdownKnowledgeGraph>();
            var validator = sp.GetRequiredService<LatCheckValidator>();
            var tracker = sp.GetRequiredService<CodeLinkTracker>();
            var enforcer = sp.GetRequiredService<TestSpecEnforcer>();
            return new LatAgentHook(kg, validator, AppContext.BaseDirectory, tracker, enforcer);
        });

        return services;
    }
}
