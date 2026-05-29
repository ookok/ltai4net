using LTAI.Core.Configuration;
using LTAI.Core.Providers;
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
        // Try to use the system's configured LLM provider for embedding (same API key).
        // Only fall back to hash-based local embedding if no API provider is configured.
        try
        {
            var sp = services.BuildServiceProvider();
            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<LTAIOptions>>();
            var provider = options?.Value?.AI?.Providers?.Values?.FirstOrDefault(p => !string.IsNullOrEmpty(p.ApiKey));
            if (provider != null && !string.IsNullOrEmpty(provider.Endpoint) && !string.IsNullOrEmpty(provider.ApiKey))
            {
                return AddLTAIVectorWithL0(services, provider.Endpoint, provider.ApiKey,
                    provider.Model ?? "BAAI/bge-large-zh-v1.5", 1024);
            }
        }
        catch { /* fall back to local hash embedding */ }

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
        // Jina local models removed — skip to API embedding fallback
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
    // AddLTAIVectorWithJina removed — ONNX/Jina local models deleted (2025-05)
    // JinaEmbeddingBackend, OnnxEmbeddingBackend, and JinaModelPresets have all been removed.

    private static IServiceCollection AddLTAIVectorInternal(
        IServiceCollection services,
        string embeddingType = "local",
        string? endpoint = null,
        string? apiKey = null,
        string? model = null,
        int? dimension = null,
        string? onnxModelPath = null)
    {
        // ONNX embedding removed — only API-based embedding is supported
        switch (embeddingType.ToLowerInvariant())
        {
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
        services.AddSingleton<KnowledgeGraph>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<KnowledgeGraph>>();
            var dataPath = sp.GetService<DataPathResolver>();
            return new KnowledgeGraph(logger, dataPath);
        });
        services.AddSingleton<RelationEngine>();
        services.AddSingleton<TemporalCompressor>();
        services.AddSingleton<SignalCleaner>();
        services.AddSingleton<StructMemory>();
        services.AddSingleton<QueryDecomposer>();
        services.AddSingleton<HybridRecallEngine>(sp =>
        {
            var llm = sp.GetRequiredService<IChatClient>();
            var vectorStore = sp.GetRequiredService<IVectorStore>();
            var docStore = sp.GetRequiredService<DocumentStore>();
            var kg = sp.GetRequiredService<KnowledgeGraph>();
            var logger = sp.GetService<ILogger<HybridRecallEngine>>();
            return new HybridRecallEngine(llm, vectorStore, docStore, kg, logger);
        });
        services.AddSingleton<AgenticRAG>(sp =>
        {
            var docStore = sp.GetRequiredService<DocumentStore>();
            var decomposer = sp.GetRequiredService<QueryDecomposer>();
            var logger = sp.GetService<ILogger<AgenticRAG>>();
            var hybrid = sp.GetService<HybridRecallEngine>();
            return new AgenticRAG(docStore, decomposer, logger, hybrid);
        });
        services.AddSingleton<DocumentIngestionPipeline>();
        services.AddSingleton<MemoryFileLoader>();
        services.AddSingleton<MemoryFilesService>(sp =>
        {
            var loader = sp.GetRequiredService<MemoryFileLoader>();
            var kg = sp.GetRequiredService<KnowledgeGraph>();
            var logger = sp.GetRequiredService<ILogger<MemoryFilesService>>();
            var booster = sp.GetService<TextRetrievalBooster>();
            return new MemoryFilesService(loader, kg, logger, booster: booster);
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

        // BM25 scorer removed; using FTS5 full-text search
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

        // KnowQL query service removed; using direct LLM query instead

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
