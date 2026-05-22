using LTAI.Core.System;
using LTAI.Knowledge.Vector.Embedding;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;

namespace LTAI.Knowledge.Vector;

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
        int onnxDiension = 384,
        bool allowJina = true,
        string? jinaModel = null)
    {
        // Auto-detect Jina: if the L0 model starts with "jina-", use the Jina backend.
        // This is the config-driven approach: just set l0.model in appsettings.json.
        if (allowJina && !string.IsNullOrEmpty(apiModel) && apiModel.StartsWith("jina-", StringComparison.OrdinalIgnoreCase))
        {
            var variant = apiModel.Contains("nano", StringComparison.OrdinalIgnoreCase)
                ? JinaModelVariant.OmniNano
                : JinaModelVariant.OmniSmall;
            return AddLTAIVectorWithJina(services, variant);
        }

        // Allow explicit Jina model override
        if (allowJina && !string.IsNullOrEmpty(jinaModel))
        {
            var variant = jinaModel.Contains("nano", StringComparison.OrdinalIgnoreCase)
                ? JinaModelVariant.OmniNano
                : JinaModelVariant.OmniSmall;
            return AddLTAIVectorWithJina(services, variant);
        }
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

    /// <summary>
    /// Configure L0 embedding to use Jina Embeddings v5 Omni.
    /// Downloads the model from HuggingFace if not cached locally.
    /// Supports jina-embeddings-v5-omni-small (768-dim) and jina-embeddings-v5-omni-nano (512-dim).
    /// </summary>
    public static IServiceCollection AddLTAIVectorWithJina(
        this IServiceCollection services,
        JinaModelVariant variant = JinaModelVariant.OmniSmall,
        string? cacheDir = null)
    {
        var preset = JinaModelPresets.GetPreset(variant);
        cacheDir ??= Path.Combine(AppContext.BaseDirectory, ".livingtree", "models", "embedding");
        var modelDir = Path.Combine(cacheDir, "jina", preset.ModelName);

        JinaEmbeddingConfig config;
        if (!File.Exists(Path.Combine(modelDir, "model.onnx")))
        {
            config = JinaModelDownloader.DownloadModelAsync(cacheDir, variant).GetAwaiter().GetResult();
        }
        else
        {
            config = new JinaEmbeddingConfig
            {
                ModelName = preset.ModelName,
                Dimension = preset.Dimension,
                HuggingFaceRepo = preset.HuggingFaceRepo,
                OnnxModelPath = Path.Combine(modelDir, "model.onnx"),
                OnnxTokenizerPath = Path.Combine(modelDir, "tokenizer.json")
            };
        }

        services.AddSingleton<JinaEmbeddingConfig>(config);
        services.AddSingleton<IEmbeddingBackend>(sp =>
        {
            var logger = sp.GetService<ILogger<JinaEmbeddingBackend>>();
            var backend = new JinaEmbeddingBackend(config, logger);
            backend.InitializeAsync().GetAwaiter().GetResult();
            return backend;
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new EmbeddingGeneratorAdapter(sp.GetRequiredService<IEmbeddingBackend>()));

        return services;
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

        services.AddSingleton<HierarchicalChunker>();
        services.AddSingleton<MultiDocFusion>();

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

        services.AddSingleton<SemanticCompactionEngine>(sp =>
        {
            var chatClient = sp.GetService<IChatClient>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            var logger = sp.GetService<ILogger<SemanticCompactionEngine>>();
            return new SemanticCompactionEngine(chatClient!, structMemory, logger);
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

        return services;
    }
}
