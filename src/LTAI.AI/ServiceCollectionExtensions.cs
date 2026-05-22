using System.ClientModel;
using LTAI.AI.Governors;
using LTAI.AI.Providers;
using LTAI.Tools.Skills;
using LTAI.Core.Configuration;
using LTAI.Core.Messaging;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace LTAI.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        services.AddSingleton<ProviderFanOutRace>();
        services.AddSingleton<BudgetTracker>();

        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var budget = sp.GetRequiredService<BudgetTracker>();
            var multiLogger = sp.GetService<ILogger<MultiProviderChatClient>>();

            var providerClients = new List<KeyValuePair<string, IChatClient>>();
            foreach (var kv in options.Value.AI.Providers)
            {
                if (string.IsNullOrEmpty(kv.Value.Endpoint))
                    continue;

                var apiKey = ResolveApiKey(kv.Key, kv.Value.ApiKey);
                if (apiKey == null)
                    continue;

                var providerClient = CreateProviderChatClient(kv.Value, apiKey, kv.Key, loggerFactory);
                if (providerClient != null)
                    providerClients.Add(new KeyValuePair<string, IChatClient>(kv.Key, providerClient));
            }

            var multiClient = new MultiProviderChatClient(providerClients, options, multiLogger!, budget);

            var pipeline = new ChatClientBuilder(multiClient)
                .UseLogging(loggerFactory)
                .UseFunctionInvocation()
                .UseOpenTelemetry()
                .UseDistributedCache()
                .Build();

            return new RescueParsingChatClient(pipeline, sp.GetService<ILogger<RescueParsingChatClient>>());
        });

        services.AddSingleton<LocalKnowledgeBase>();
        services.AddSingleton<LocalIntentClassifier>();
        services.AddSingleton<SkillCatalog>();

        services.AddSingleton<HybridIntentRouter>(sp =>
        {
            var local = sp.GetRequiredService<LocalIntentClassifier>();
            var embedder = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            var logger = sp.GetService<ILogger<HybridIntentRouter>>();
            return new HybridIntentRouter(local, embedder, logger);
        });

        services.AddSingleton<KnowledgeGraph>(sp =>
        {
            var logger = sp.GetService<ILogger<KnowledgeGraph>>();
            var graph = new LTAI.Knowledge.Core.KnowledgeGraph(logger!);
            var graphPath = System.IO.Path.Combine(AppContext.BaseDirectory, ".livingtree", "knowledge_graph.json");
            if (System.IO.File.Exists(graphPath))
                graph.LoadFromDisk(graphPath);
            return graph;
        });

        var synapticDir = System.IO.Path.Combine(AppContext.BaseDirectory, "synaptic");
        services.AddSingleton<DualMemoryStore>(sp =>
        {
            var logger = sp.GetService<ILogger<DualMemoryStore>>();
            var dbPath = System.IO.Path.Combine(synapticDir, "dual_memory.db");
            Directory.CreateDirectory(synapticDir);
            var embeddingGenerator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            return new DualMemoryStore(dbPath, config: null, retrievalWeights: null, embeddingGenerator, logger);
        });

        services.AddSingleton<MemoryQualityMonitor>(sp =>
        {
            var memoryStore = sp.GetRequiredService<DualMemoryStore>();
            var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
            var logger = sp.GetService<ILogger<MemoryQualityMonitor>>();
            return new MemoryQualityMonitor(memoryStore, cellRegistry, logger);
        });

        services.AddSingleton<IncrementalRuleExtractor>(sp =>
        {
            var memoryStore = sp.GetRequiredService<DualMemoryStore>();
            var logger = sp.GetService<ILogger<IncrementalRuleExtractor>>();
            return new IncrementalRuleExtractor(memoryStore, logger);
        });

        // Fast-Slow Learning 组件
        services.AddSingleton<GEPAPromptOptimizer>(sp =>
        {
            var logger = sp.GetService<ILogger<GEPAPromptOptimizer>>();
            return new GEPAPromptOptimizer(config: null, logger);
        });

        services.AddSingleton<FastSlowCellAI>(sp =>
        {
            var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
            var memoryStore = sp.GetRequiredService<DualMemoryStore>();
            var promptOptimizer = sp.GetRequiredService<GEPAPromptOptimizer>();
            var logger = sp.GetService<ILogger<FastSlowCellAI>>();
            return new FastSlowCellAI(cellRegistry, memoryStore, promptOptimizer, config: null, logger);
        });

        services.AddSingleton<SynapticMemory>(sp =>
        {
            var logger = sp.GetService<ILogger<SynapticMemory>>();
            var dbPath = System.IO.Path.Combine(synapticDir, "synaptic_memory.db");
            Directory.CreateDirectory(synapticDir);
            return new SynapticMemory(dbPath, logger);
        });

        services.AddSingleton<SynapticTrainer>(sp =>
        {
            var logger = sp.GetService<ILogger<SynapticTrainer>>();
            var modelDir = System.IO.Path.Combine(synapticDir, "models");
            return new SynapticTrainer(modelDir, logger);
        });

        services.AddSingleton<SynapticInference>(sp =>
        {
            var logger = sp.GetService<ILogger<SynapticInference>>();
            var inference = new SynapticInference(logger);
            var trainer = sp.GetRequiredService<SynapticTrainer>();
            var existingModel = trainer.GetLatestModelPath();
            if (existingModel != null)
                inference.LoadModel(existingModel);
            return inference;
        });

        services.AddSingleton<KnowledgeGraphBridge>(sp =>
        {
            var graph = sp.GetRequiredService<KnowledgeGraph>();
            var logger = sp.GetService<ILogger<KnowledgeGraphBridge>>();
            return new KnowledgeGraphBridge(graph, logger);
        });

        services.AddSingleton<MetaCognitiveLayer>(sp =>
        {
            var logger = sp.GetService<ILogger<MetaCognitiveLayer>>();
            return new MetaCognitiveLayer(logger);
        });

        services.AddSingleton<SkillTree>(sp =>
        {
            var catalog = sp.GetRequiredService<SkillCatalog>();
            var logger = sp.GetService<ILogger<SkillTree>>();
            return new SkillTree(catalog, logger);
        });

        services.AddSingleton<CellAnswerStore>();
        services.AddSingleton<SemanticQueryCache>();
        services.AddSingleton<TeachingRuleExtractor>(sp =>
        {
            var answerStore = sp.GetRequiredService<CellAnswerStore>();
            var logger = sp.GetService<ILogger<TeachingRuleExtractor>>();
            return new TeachingRuleExtractor(answerStore, logger);
        });
        services.AddSingleton<CostAwareRouter>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>>();
            var logger = sp.GetService<ILogger<CostAwareRouter>>();
            return new CostAwareRouter(options, logger);
        });
        services.AddSingleton<KnowledgeGapDetector>(sp =>
        {
            var metaCognition = sp.GetRequiredService<MetaCognitiveLayer>();
            var logger = sp.GetService<ILogger<KnowledgeGapDetector>>();
            return new KnowledgeGapDetector(metaCognition, logger);
        });
        services.AddSingleton<MultimodalRouter>();
        services.AddSingleton<AbTestingFramework>();

        services.AddSingleton<CellAIRegistry>(sp =>
        {
            var answerStore = sp.GetRequiredService<CellAnswerStore>();
            var trainer = sp.GetRequiredService<SynapticTrainer>();
            var memory = sp.GetRequiredService<SynapticMemory>();
            var logger = sp.GetService<ILogger<CellAIRegistry>>();
            var registry = new CellAIRegistry(answerStore, trainer, memory, logger);
            
            // 配置混合策略
            registry.ConfigureHybridStrategy(
                selfTrainedOverrideThreshold: 0.75f,
                fallbackToSelfTrained: true);
            
            return registry;
        });

        // ==================== GitHub 细胞分发系统 ====================

        services.AddSingleton<CellPackageManager>(sp =>
        {
            var logger = sp.GetService<ILogger<CellPackageManager>>();
            var packagesDir = System.IO.Path.Combine(synapticDir, "packages");
            return new CellPackageManager(packagesDir, logger!);
        });

        services.AddSingleton<GitHubCellRegistry>(sp =>
        {
            var packageManager = sp.GetRequiredService<CellPackageManager>();
            var logger = sp.GetService<ILogger<GitHubCellRegistry>>();
            var config = new GitHubCellConfig
            {
                Owner = "ltai-org",
                Repository = "ltai-cells",
                Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
                MaxDownloadSizeMB = 100
            };
            return new GitHubCellRegistry(config, packageManager, logger!);
        });

        services.AddSingleton<SizeGovernor>(sp =>
        {
            var packageManager = sp.GetRequiredService<CellPackageManager>();
            var logger = sp.GetService<ILogger<SizeGovernor>>();
            var config = new SizeGovernorConfig
            {
                MaxCellSizeMB = 50,
                MaxTotalSizeMB = 500,
                EnableAutoCompression = true,
                EnableQuantization = true
            };
            return new SizeGovernor(config, packageManager, logger);
        });

        services.AddSingleton<CascadeLoader>(sp =>
        {
            var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
            var githubRegistry = sp.GetRequiredService<GitHubCellRegistry>();
            var packageManager = sp.GetRequiredService<CellPackageManager>();
            var logger = sp.GetService<ILogger<CascadeLoader>>();
            var config = new CascadeLoaderConfig
            {
                MaxConcurrentDownloads = 3,
                MaxMemoryMB = 200,
                EnableLazyLoading = true,
                EnableAutoUnload = true,
                MaxCachedCells = 20
            };
            return new CascadeLoader(config, cellRegistry, githubRegistry, packageManager, logger!);
        });

        // ==================== 领域知识图谱分发系统 ====================

        services.AddSingleton<DomainGraphRegistry>(sp =>
        {
            var logger = sp.GetService<ILogger<DomainGraphRegistry>>();
            var config = new DomainGraphConfig
            {
                GraphsDirectory = System.IO.Path.Combine(synapticDir, "graphs"),
                MaxLoadedGraphs = 10,
                EnableLazyLoading = true,
                EnableAutoUnload = true
            };
            return new DomainGraphRegistry(config, logger!);
        });

        services.AddSingleton<GraphPackageManager>(sp =>
        {
            var logger = sp.GetService<ILogger<GraphPackageManager>>();
            var packagesDir = System.IO.Path.Combine(synapticDir, "graph_packages");
            return new GraphPackageManager(packagesDir, logger!);
        });

        services.AddSingleton<GitHubGraphRegistry>(sp =>
        {
            var packageManager = sp.GetRequiredService<GraphPackageManager>();
            var logger = sp.GetService<ILogger<GitHubGraphRegistry>>();
            var config = new GitHubGraphConfig
            {
                Owner = "ltai-org",
                Repository = "ltai-graphs",
                Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
                MaxDownloadSizeMB = 200
            };
            return new GitHubGraphRegistry(config, packageManager, logger!);
        });

        services.AddSingleton<GraphCascadeLoader>(sp =>
        {
            var graphRegistry = sp.GetRequiredService<DomainGraphRegistry>();
            var githubRegistry = sp.GetRequiredService<GitHubGraphRegistry>();
            var packageManager = sp.GetRequiredService<GraphPackageManager>();
            var logger = sp.GetService<ILogger<GraphCascadeLoader>>();
            var config = new GraphCascadeConfig
            {
                MaxConcurrentDownloads = 2,
                MaxMemoryMB = 300,
                EnableLazyLoading = true,
                EnableAutoUnload = true,
                MaxLoadedGraphs = 10
            };
            return new GraphCascadeLoader(config, graphRegistry, githubRegistry, packageManager, logger!);
        });

        // ==================== 事件驱动计划治理 ====================

        services.AddSingleton<FastSlowGovernorPipeline>(sp =>
        {
            var eventBus = sp.GetRequiredService<IEventBusV2>();
            var planStore = sp.GetRequiredService<CellAnswerStore>();
            var fastSlowAI = sp.GetRequiredService<FastSlowCellAI>();
            var memoryStore = sp.GetRequiredService<DualMemoryStore>();
            var logger = sp.GetService<ILogger<FastSlowGovernorPipeline>>();
            var config = new PlanGovernorConfig
            {
                EnableAutoInvalidation = true,
                HighImpactPriorityThreshold = 5,
                ReplanningCooldown = TimeSpan.FromMinutes(5),
                MaxReplansPerHour = 10
            };
            return new FastSlowGovernorPipeline(eventBus, planStore, fastSlowAI, memoryStore, config, logger!);
        });

        services.AddSingleton<DomainDiscoveryService>(sp =>
        {
            var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
            var logger = sp.GetService<ILogger<DomainDiscoveryService>>();
            var config = new DomainDiscoveryConfig
            {
                MinQueriesToDiscover = 10,
                SimilarityThreshold = 0.3f,
                DiscoveryInterval = TimeSpan.FromMinutes(30),
                MaxNurserySize = 1000
            };
            return new DomainDiscoveryService(config, cellRegistry, logger!);
        });

        services.AddSingleton<IL1InferenceEngine>(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>();
            var modelDir = System.IO.Path.Combine(synapticDir, "models", "local_llm");
            
            // 尝试读取用户配置
            var userConfigPath = System.IO.Path.Combine(AppContext.BaseDirectory, "local_llm.json");
            string? preferredEngine = null;
            if (System.IO.File.Exists(userConfigPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(userConfigPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("EngineType", out var engineProp))
                        preferredEngine = engineProp.GetString();
                }
                catch { }
            }

            // 根据配置选择引擎
            if (preferredEngine?.Equals("gguf", StringComparison.OrdinalIgnoreCase) == true)
            {
                sp.GetService<ILogger<LlamaSharpEngine>>();
                return new LlamaSharpEngine(logger?.CreateLogger<LlamaSharpEngine>());
            }
            else
            {
                var modelPath = System.IO.Path.Combine(modelDir, "model.onnx");
                var tokenizerPath = System.IO.Path.Combine(modelDir, "tokenizer.json");
                
                var config = new SmallLlmConfig
                {
                    ModelPath = modelPath,
                    TokenizerPath = tokenizerPath,
                    ModelName = "local-quantized-llm",
                    MaxContextLength = 2048
                };

                return new OnnxSmallLlmEngine(config, logger?.CreateLogger<OnnxSmallLlmEngine>());
            }
        });

        services.AddSingleton<LocalLlmBootstrapConfig>(sp =>
        {
            var modelDir = System.IO.Path.Combine(synapticDir, "models", "local_llm");
            return new LocalLlmBootstrapConfig
            {
                ModelDir = modelDir,
                AutoDownloadIfMissing = true,
                AutoUpdate = false
            };
        });

        services.AddHttpClient(); // 确保 IHttpClientFactory 可用
        services.AddHostedService<LocalLlmBootstrapService>(sp =>
        {
            var config = sp.GetRequiredService<LocalLlmBootstrapConfig>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var engine = sp.GetRequiredService<IL1InferenceEngine>();
            var logger = sp.GetService<ILogger<LocalLlmBootstrapService>>();
            return new LocalLlmBootstrapService(config, httpClientFactory, engine, logger!);
        });

        services.AddHostedService<PretrainedModelLoader>(sp =>
        {
            var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
            var logger = sp.GetService<ILogger<PretrainedModelLoader>>();
            return new PretrainedModelLoader(cellRegistry, logger);
        });

        services.AddSingleton<TokenHardnessDecider>();
        services.AddSingleton<SelectiveThinkingPipeline>(sp =>
        {
            var l1Engine = sp.GetService<IL1InferenceEngine>();
            var l2Client = sp.GetService<IChatClient>();
            var decider = sp.GetRequiredService<TokenHardnessDecider>();
            var logger = sp.GetService<ILogger<SelectiveThinkingPipeline>>();
            return l1Engine != null && l2Client != null 
                ? new SelectiveThinkingPipeline(l1Engine, l2Client, decider, logger) 
                : null!;
        });

        services.AddSingleton<L1L2DuplexRouter>(sp =>
        {
            var inference = sp.GetRequiredService<SynapticInference>();
            var memory = sp.GetRequiredService<SynapticMemory>();
            var graphBridge = sp.GetRequiredService<KnowledgeGraphBridge>();
            var domainGraphRegistry = sp.GetRequiredService<DomainGraphRegistry>();
            var domainDiscovery = sp.GetRequiredService<DomainDiscoveryService>();
            var localLlm = sp.GetService<IL1InferenceEngine>();
            var metaCognition = sp.GetRequiredService<MetaCognitiveLayer>();
            var skillTree = sp.GetRequiredService<SkillTree>();
            var cache = sp.GetRequiredService<SemanticQueryCache>();
            var ruleExtractor = sp.GetRequiredService<TeachingRuleExtractor>();
            var costRouter = sp.GetRequiredService<CostAwareRouter>();
            var knowledge = sp.GetRequiredService<LocalKnowledgeBase>();
            var classifier = sp.GetRequiredService<LocalIntentClassifier>();
            var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
            var llm = sp.GetService<IChatClient>();
            var logger = sp.GetService<ILogger<L1L2DuplexRouter>>();
            return new L1L2DuplexRouter(inference, memory, graphBridge, domainGraphRegistry, domainDiscovery, localLlm, metaCognition, skillTree, cache, ruleExtractor, costRouter, knowledge, classifier, cellRegistry, llm, logger);
        });

        services.AddHostedService<SynapticEvolutionLoop>(sp =>
        {
            var memory = sp.GetRequiredService<SynapticMemory>();
            var trainer = sp.GetRequiredService<SynapticTrainer>();
            var inference = sp.GetRequiredService<SynapticInference>();
            var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
            var logger = sp.GetService<ILogger<SynapticEvolutionLoop>>();
            return new SynapticEvolutionLoop(memory, trainer, inference, cellRegistry, logger);
        });

        services.AddHostedService<DreamCycle>(sp =>
        {
            var memory = sp.GetRequiredService<SynapticMemory>();
            var graphBridge = sp.GetRequiredService<KnowledgeGraphBridge>();
            var skillTree = sp.GetRequiredService<SkillTree>();
            var metaCognition = sp.GetRequiredService<MetaCognitiveLayer>();
            var dualMemoryStore = sp.GetRequiredService<DualMemoryStore>();
            var ruleExtractor = sp.GetRequiredService<IncrementalRuleExtractor>();
            var logger = sp.GetService<ILogger<DreamCycle>>();
            return new DreamCycle(memory, graphBridge, skillTree, metaCognition, dualMemoryStore, ruleExtractor, logger);
        });

        services.AddHostedService<FederatedLearningService>(sp =>
        {
            var transport = sp.GetService<IFederatedTransport>();
            var trainer = sp.GetRequiredService<SynapticTrainer>();
            var inference = sp.GetRequiredService<SynapticInference>();
            var memory = sp.GetRequiredService<SynapticMemory>();
            var logger = sp.GetService<ILogger<FederatedLearningService>>();
            return new FederatedLearningService(transport, trainer, inference, memory, logger);
        });

        services.AddSingleton<InputGovernor>();
        services.AddSingleton<ContextGovernor>();
        services.AddSingleton<RoutingGovernor>();
        services.AddSingleton<OutputGovernor>();
        services.AddSingleton<SelfGovernor>();
        services.AddSingleton<SystemGuardian>();
        services.AddSingleton<LivingTreeSystem>();

        return services;
    }

    private static string? ResolveApiKey(string providerName, string configuredKey)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey) && !configuredKey.Contains("YOUR_API_KEY", StringComparison.OrdinalIgnoreCase))
            return configuredKey;

        var nameUpper = providerName.ToUpperInvariant();

        var isLocal = nameUpper is "OLLAMA" or "LMSTUDIO" or "LM_STUDIO" or "VLLM" or "LLAMACPP" or "LLAMA_CPP" or "OPEN_WEBUI";

        var envVar = nameUpper switch
        {
            "DEEPSEEK" => "DEEPSEEK_API_KEY",
            "OPENAI" => "OPENAI_API_KEY",
            "ANTHROPIC" => "ANTHROPIC_API_KEY",
            "GEMINI" or "GOOGLE" => "GEMINI_API_KEY",
            "SILICONFLOW" => "SILICONFLOW_API_KEY",
            _ => isLocal ? null : $"{nameUpper}_API_KEY"
        };

        var envKey = envVar != null ? Environment.GetEnvironmentVariable(envVar) : null;
        if (!string.IsNullOrEmpty(envKey))
            return envKey;

        if (isLocal)
            return "";

        try { return SecretVault.Instance.Get(providerName); }
        catch { return null; }
    }

    private static IChatClient? CreateProviderChatClient(
        ProviderConfig config, string apiKey, string providerName, ILoggerFactory loggerFactory)
    {
        try
        {
            var endpoint = config.Endpoint.TrimEnd('/');
            var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey);
            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
            var openAiClient = new OpenAIClient(credential, clientOptions);
            var chatClient = openAiClient.GetChatClient(config.Model);
            return new OpenAIProviderChatClient(chatClient);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("LTAI.AI").LogWarning(ex, "Skipping provider {Provider}: creation failed", providerName);
            return null;
        }
    }
}
