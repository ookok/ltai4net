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
        return AddLTAIVectorInternal(services, embeddingType: "local");
    }

    public static IServiceCollection AddLTAIVectorWithL0(
        this IServiceCollection services,
        string endpoint,
        string apiKey,
        string model = "BAAI/bge-large-zh-v1.5",
        int dimension = 1024)
    {
        return AddLTAIVectorInternal(services, embeddingType: "api", endpoint, apiKey, model, dimension, onnxModelPath: null);
    }

    public static IServiceCollection AddLTAIVectorWithOnnx(
        this IServiceCollection services,
        string modelPath,
        string? tokenizerPath = null,
        int dimension = 384,
        string modelName = "onnx-embedding")
    {
        return AddLTAIVectorInternal(services, embeddingType: "onnx", endpoint: null, apiKey: null, model: modelName, dimension, modelPath);
    }

    public static IServiceCollection AddLTAIVectorAuto(
        this IServiceCollection services,
        string? apiEndpoint = null,
        string? apiKey = null,
        string apiModel = "BAAI/bge-large-zh-v1.5",
        int apiDimension = 1024,
        string? onnxModelPath = null,
        int onnxDiension = 384)
    {
        if (!string.IsNullOrEmpty(apiEndpoint) && !string.IsNullOrEmpty(apiKey))
        {
            return AddLTAIVectorWithL0(services, apiEndpoint, apiKey, apiModel, apiDimension);
        }
        if (!string.IsNullOrEmpty(onnxModelPath) && System.IO.File.Exists(onnxModelPath))
        {
            return AddLTAIVectorWithOnnx(services, onnxModelPath, dimension: onnxDiension);
        }
        return AddLTAIVector(services);
    }

    private static IServiceCollection AddLTAIVectorInternal(
        IServiceCollection services,
        string embeddingType = "local",
        string? endpoint = null,
        string? apiKey = null,
        string? model = null,
        int? dimension = null,
        string? onnxModelPath = null)
    {
        switch (embeddingType.ToLowerInvariant())
        {
            case "onnx":
                services.AddSingleton<IEmbeddingBackend>(sp =>
                {
                    var config = new OnnxEmbeddingConfig
                    {
                        ModelPath = onnxModelPath!,
                        TokenizerPath = null,
                        Dimension = dimension ?? 384,
                        ModelName = model ?? "onnx-embedding"
                    };
                    var backend = new OnnxEmbeddingBackend(config, sp.GetRequiredService<ILogger<OnnxEmbeddingBackend>>());
                    
                    // 异步初始化 (Fire and Forget with logging)
                    _ = backend.InitializeAsync().ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            sp.GetRequiredService<ILogger<OnnxEmbeddingBackend>>().LogError(t.Exception, "ONNX Embedding init failed");
                        }
                    });

                    return backend;
                });
                break;

            case "api":
                services.AddSingleton<IEmbeddingBackend>(sp =>
                    new APIEmbeddingBackend(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        endpoint!, apiKey!, model!, dimension ?? 1024,
                        sp.GetRequiredService<ILogger<APIEmbeddingBackend>>()));
                break;

            default: // local
                services.AddSingleton<IEmbeddingBackend, LocalEmbeddingBackend>();
                break;
        }

        services.AddSingleton<EmbeddingQuantizer>();
        services.AddSingleton<LazyResultDeserializer>();

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new EmbeddingGeneratorAdapter(sp.GetRequiredService<IEmbeddingBackend>()));

        services.AddSingleton<IVectorStore, VectorStore>();
        services.AddSingleton<DocumentStore>(sp =>
        {
            return new DocumentStore(
                sp.GetRequiredService<DataPathResolver>(),
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
            return new CompiledTruthStore(sp.GetRequiredService<DataPathResolver>());
        });
        services.AddSingleton<UnifiedBrainStore>(sp =>
        {
            return new UnifiedBrainStore(
                sp.GetRequiredService<DataPathResolver>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<ILogger<UnifiedBrainStore>>());
        });

        services.AddSingleton<MemoryPoisoningDefense>();

        services.AddSingleton<SemanticCompactionEngine>(sp =>
        {
            var chatClient = sp.GetService<IChatClient>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            var logger = sp.GetService<ILogger<SemanticCompactionEngine>>();
            return new SemanticCompactionEngine(chatClient!, structMemory, logger);
        });

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
