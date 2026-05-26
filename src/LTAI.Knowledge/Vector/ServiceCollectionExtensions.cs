using LTAI.Core.Configuration;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Services;
using LTAI.Knowledge.Vector.Embedding;
using LTAI.Knowledge.Vector.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.KernelMemory;

namespace LTAI.Knowledge.Vector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIVector(this IServiceCollection services)
    {
        return AddLTAIVectorLocal(services);
    }

    public static IServiceCollection AddLTAIVectorLocal(
        this IServiceCollection services,
        string? onnxModelPath = null,
        string? tokenizerPath = null,
        int dimension = 384)
    {
        var modelPath = onnxModelPath ?? Path.Combine(OptionService.Get("paths.models") ?? Path.Combine(AppContext.BaseDirectory, "models"), "l0", "model.onnx");
        var tokPath = tokenizerPath ?? Path.Combine(Path.GetDirectoryName(modelPath)!, "tokenizer.json");

        if (File.Exists(modelPath))
        {
            return AddLTAIVectorWithOnnx(services, modelPath, tokPath, dimension, "bge-small-zh-v1.5");
        }

        // Fallback to hash-based local embedding (no model needed)
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
        cacheDir ??= Path.Combine(OptionService.Get("paths.livingtree") ?? Path.Combine(AppContext.BaseDirectory, ".livingtree"), "models", "embedding");
        var modelDir = Path.Combine(cacheDir, "jina", preset.ModelName);

        JinaEmbeddingConfig config;
        if (!File.Exists(Path.Combine(modelDir, "model.onnx")))
        {
            config = Task.Run(() => JinaModelDownloader.DownloadModelAsync(cacheDir, variant)).GetAwaiter().GetResult();
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
#pragma warning disable CS0618
            var logger = sp.GetService<ILogger<JinaEmbeddingBackend>>();
            var backend = new JinaEmbeddingBackend(config, logger);
            Task.Run(() => backend.InitializeAsync()).GetAwaiter().GetResult();
            return backend;
        });
#pragma warning restore CS0618

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            CreateWrappedGenerator(sp, sp.GetRequiredService<IEmbeddingBackend>()));

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
#pragma warning disable CS0618
                    new APIEmbeddingBackend(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        endpoint!, apiKey!, model!, dimension ?? 1024,
                        sp.GetRequiredService<ILogger<APIEmbeddingBackend>>()));
#pragma warning restore CS0618
                break;

            default: // local
                services.AddSingleton<IEmbeddingBackend, LocalEmbeddingBackend>();
                break;
        }

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            CreateWrappedGenerator(sp, sp.GetRequiredService<IEmbeddingBackend>()));

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
        services.AddSingleton<RelationEngine>();
        services.AddSingleton<TemporalCompressor>();
        services.AddSingleton<SignalCleaner>();
        services.AddSingleton<StructMemory>();
        services.AddSingleton<Reranker>();
        services.AddSingleton<QueryDecomposer>();
        services.AddSingleton<HybridRecallEngine>(sp =>
        {
            var llm = sp.GetRequiredService<IChatClient>();
            var vectorStore = sp.GetRequiredService<IVectorStore>();
            var docStore = sp.GetRequiredService<DocumentStore>();
            var kg = sp.GetRequiredService<KnowledgeGraph>();
            var reranker = sp.GetRequiredService<Reranker>();
            var logger = sp.GetService<ILogger<HybridRecallEngine>>();
            return new HybridRecallEngine(llm, vectorStore, docStore, kg, reranker, logger);
        });
        services.AddSingleton<AgenticRAG>(sp =>
        {
            var docStore = sp.GetRequiredService<DocumentStore>();
            var reranker = sp.GetRequiredService<Reranker>();
            var decomposer = sp.GetRequiredService<QueryDecomposer>();
            var logger = sp.GetService<ILogger<AgenticRAG>>();
            var hybrid = sp.GetService<HybridRecallEngine>();
            return new AgenticRAG(docStore, reranker, decomposer, logger, hybrid);
        });
        services.AddSingleton<DocumentIngestionPipeline>();
        services.AddSingleton<MemoryFileLoader>();
        services.AddSingleton<MemoryFilesService>(sp =>
        {
            var loader = sp.GetRequiredService<MemoryFileLoader>();
            var kg = sp.GetRequiredService<KnowledgeGraph>();
            var logger = sp.GetRequiredService<ILogger<MemoryFilesService>>();
            return new MemoryFilesService(loader, kg, logger);
        });
        services.AddSingleton<PromptLoader>();
        services.AddSingleton<PromptService>(sp =>
        {
            var loader = sp.GetRequiredService<PromptLoader>();
            var logger = sp.GetRequiredService<ILogger<PromptService>>();
            return new PromptService(loader, logger);
        });

        services.AddSingleton<PromptAbTestManager>(sp =>
        {
            var promptService = sp.GetRequiredService<PromptService>();
            var logger = sp.GetRequiredService<ILogger<PromptAbTestManager>>();
            var mgr = new PromptAbTestManager(promptService, logger);
            promptService.SetAbTestManager(mgr);
            return mgr;
        });

        services.AddSingleton<OptionLoader>();
        services.AddSingleton<OptionService>(sp =>
        {
            var loader = sp.GetRequiredService<OptionLoader>();
            var defaults = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var config = sp.GetService<IConfiguration>();
            var logger = sp.GetRequiredService<ILogger<OptionService>>();
            return new OptionService(loader, defaults, config, logger);
        });

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

        // Markdown-based knowledge graph (lat.md folder)
        services.AddSingleton(sp =>
        {
            var rootPath = OptionService.Get("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory();
            var embedding = sp.GetService<IEmbeddingBackend>();
            var vectorStore = sp.GetService<IVectorStore>();
            var graph = new MarkdownKnowledgeGraph(rootPath, embedding, vectorStore);
            graph.Initialize();
            return graph;
        });

        // Code graph auto-index background service
        services.AddHostedService<CodeGraphSyncService>();

        return services;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateWrappedGenerator(
        IServiceProvider sp,
        IEmbeddingBackend backend)
    {
        var adapter = new EmbeddingGeneratorAdapter(backend);

        var options = sp.GetService<IOptions<LTAIOptions>>();
        if (options?.Value?.Vector?.TaskAwareEmbedding == true)
        {
            return new TaskAwareEmbeddingGeneratorDecorator(adapter);
        }

        return adapter;
    }
}
