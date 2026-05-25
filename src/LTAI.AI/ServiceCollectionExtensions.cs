using System.ClientModel;
using LTAI.AI.Governors;
using LTAI.AI.Providers;
using LTAI.Tools.Skills;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;
using LTAI.Core.Messaging;
using LTAI.Core.Network;
using LTAI.Core.System;
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
        services.AddSingleton<ProviderFanOutRace>(sp =>
        {
            var logger = sp.GetService<ILogger<ProviderFanOutRace>>();
            return new ProviderFanOutRace(
                Array.Empty<IChatClient>(),
                Array.Empty<string>(),
                "primary",
                logger);
        });
        services.AddSingleton<BudgetTracker>();
        services.AddSingleton<PrefixCacheStore>();

        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var budget = sp.GetRequiredService<BudgetTracker>();
            var prefixCache = sp.GetRequiredService<PrefixCacheStore>();
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

            var multiClient = new MultiProviderChatClient(providerClients, options, multiLogger!, budget, prefixCache);

            var pipeline = new ChatClientBuilder(multiClient)
                .UseLogging(loggerFactory)
                .UseFunctionInvocation(loggerFactory, client =>
                {
                    client.MaximumIterationsPerRequest = int.MaxValue;
                    client.AllowConcurrentInvocation = true;
                })
                .UseOpenTelemetry()
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
            var logger = sp.GetRequiredService<ILogger<KnowledgeGraph>>();
            var dataPath = sp.GetRequiredService<DataPathResolver>();
            return new LTAI.Knowledge.Core.KnowledgeGraph(logger, dataPath);
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

        services.AddSingleton<ICrossRunEvolutionStore>(sp =>
        {
            var dbPath = System.IO.Path.Combine(synapticDir, "crossrun_evolution.db");
            Directory.CreateDirectory(synapticDir);
            var options = sp.GetService<IOptions<LTAIOptions>>();
            var halfLife = options?.Value.Thresholds.CrossRunHalfLifeDays ?? 30;
            return new CrossRunEvolutionStore(dbPath, halfLife);
        });

        services.AddSingleton<IVerifiableRegistry>(sp =>
        {
            var dbPath = System.IO.Path.Combine(synapticDir, "numeric_registry.db");
            Directory.CreateDirectory(synapticDir);
            var registry = new VerifiableRegistry(dbPath);
            var logger = sp.GetService<ILogger<IVerifiableRegistry>>();
            if (logger != null)
            {
                registry.OnClaimRejected += (claim, result) =>
                    logger.LogWarning("Numeric claim rejected: {Claim} — {Discrepancy}", claim, result.Discrepancy);
            }
            return registry;
        });

        services.AddSingleton<AdaptiveDepthController>(sp =>
        {
            var logger = sp.GetService<ILogger<AdaptiveDepthController>>();
            var paceTracker = sp.GetService<LearningProgressTracker>();
            return new AdaptiveDepthController(logger, paceTracker);
        });

        services.AddSingleton<TieredLoraManager>(sp =>
        {
            var logger = sp.GetService<ILogger<TieredLoraManager>>();
            var modelDir = System.IO.Path.Combine(synapticDir, "models");
            var depthController = sp.GetRequiredService<AdaptiveDepthController>();
            return new TieredLoraManager(modelDir, depthController, logger);
        });

        services.AddSingleton<CrossLevelDistiller>(sp =>
        {
            var loraManager = sp.GetRequiredService<TieredLoraManager>();
            var depthController = sp.GetRequiredService<AdaptiveDepthController>();
            var logger = sp.GetService<ILogger<CrossLevelDistiller>>();
            return new CrossLevelDistiller(loraManager, depthController, logger);
        });

        services.AddSingleton<CorrectionMemory>(sp =>
        {
            var llm = sp.GetRequiredService<IChatClient>();
            var synapticMemory = sp.GetRequiredService<SynapticMemory>();
            var logger = sp.GetService<ILogger<CorrectionMemory>>();
            var correctionGate = sp.GetService<Gdn2CorrectionGate>();
            return new CorrectionMemory(llm, synapticMemory, logger, correctionGate: correctionGate);
        });

        services.AddSingleton<Gdn2CorrectionGate>(_ => new Gdn2CorrectionGate(dimK: 128, dimV: 128, maxStates: 32));

        services.AddSingleton<SelfCorrectionLoRA>(sp =>
        {
            var loraManager = sp.GetRequiredService<TieredLoraManager>();
            var depthController = sp.GetRequiredService<AdaptiveDepthController>();
            var logger = sp.GetService<ILogger<SelfCorrectionLoRA>>();
            return new SelfCorrectionLoRA(loraManager, depthController, logger);
        });

        services.AddSingleton<SpinSelfPlayLoop>(sp =>
        {
            var loraManager = sp.GetRequiredService<TieredLoraManager>();
            var depthController = sp.GetRequiredService<AdaptiveDepthController>();
            var synapticMemory = sp.GetService<SynapticMemory>();
            var correctionMemory = sp.GetService<CorrectionMemory>();
            var correctionLoRA = sp.GetService<SelfCorrectionLoRA>();
            var logger = sp.GetService<ILogger<SpinSelfPlayLoop>>();
            return new SpinSelfPlayLoop(loraManager, depthController, synapticMemory, correctionMemory, correctionLoRA, logger);
        });

        services.AddSingleton<MoERouter>(sp =>
        {
            var depthController = sp.GetRequiredService<AdaptiveDepthController>();
            var logger = sp.GetService<ILogger<MoERouter>>();
            return new MoERouter(depthController, logger);
        });

        services.AddSingleton<NeuralDependencyGraph>(sp =>
        {
            var logger = sp.GetService<ILogger<NeuralDependencyGraph>>();
            return new NeuralDependencyGraph(logger);
        });

        services.AddSingleton<StructureAwareRouter>(sp =>
        {
            var loraManager = sp.GetRequiredService<TieredLoraManager>();
            var depGraph = sp.GetRequiredService<NeuralDependencyGraph>();
            var logger = sp.GetService<ILogger<StructureAwareRouter>>();
            return new StructureAwareRouter(loraManager, depGraph, logger);
        });

        services.AddSingleton<CapabilityMigrator>(sp =>
        {
            var loraManager = sp.GetRequiredService<TieredLoraManager>();
            var synapticMemory = sp.GetService<SynapticMemory>();
            var logger = sp.GetService<ILogger<CapabilityMigrator>>();
            return new CapabilityMigrator(loraManager, synapticMemory, logger);
        });

        services.AddSingleton<ModelUpgrader>(sp =>
        {
            var loraManager = sp.GetRequiredService<TieredLoraManager>();
            var migrator = sp.GetRequiredService<CapabilityMigrator>();
            var synapticMemory = sp.GetService<SynapticMemory>();
            var logger = sp.GetService<ILogger<ModelUpgrader>>();
            return new ModelUpgrader(loraManager, migrator, synapticMemory, logger);
        });

        services.AddSingleton<ModelAutoDownloader>(sp =>
        {
            var logger = sp.GetService<ILogger<ModelAutoDownloader>>();
            var modelsRoot = global::System.IO.Path.Combine(AppContext.BaseDirectory, "models");
            return new ModelAutoDownloader(modelsRoot, logger);
        });

        services.AddSingleton<SpeculativeDecoder>(sp =>
        {
            var logger = sp.GetService<ILogger<SpeculativeDecoder>>();
            var config = new SpeculativeDecoderConfig
            {
                DraftSteps = 6,
                DraftModelPath = global::System.IO.Path.Combine(synapticDir, "models", "smollm2-135m", "model.onnx"),
                DraftTokenizerPath = global::System.IO.Path.Combine(synapticDir, "models", "smollm2-135m", "tokenizer.json")
            };
            return new SpeculativeDecoder(config, logger);
        });

        services.AddSingleton<LoraTrainer>(sp =>
        {
            var logger = sp.GetService<ILogger<LoraTrainer>>();
            var modelDir = System.IO.Path.Combine(synapticDir, "models");
            return new LoraTrainer(modelDir, logger);
        });

        services.AddSingleton<SynapticTrainer>(sp =>
        {
            var logger = sp.GetService<ILogger<SynapticTrainer>>();
            var modelDir = System.IO.Path.Combine(synapticDir, "models");
            var loraTrainer = sp.GetService<LoraTrainer>();
            return new SynapticTrainer(modelDir, logger, loraTrainer);
        });

        services.AddSingleton<SynapticInference>(sp =>
        {
            var logger = sp.GetService<ILogger<SynapticInference>>();
            var loraTrainer = sp.GetService<LoraTrainer>();
            var inference = new SynapticInference(logger, loraTrainer);

            // Auto-load latest model (LoRA weights > ONNX > ML.NET ZIP)
            if (loraTrainer != null)
            {
                var latestWeights = loraTrainer.GetLatestWeightsPath();
                if (latestWeights != null)
                    inference.LoadLoraWeights(latestWeights);
            }
            else
            {
                var trainer = sp.GetRequiredService<SynapticTrainer>();
                var existing = trainer.GetLatestModelPath()
                    ?? trainer.GetLatestOnnxPath();
                if (existing != null)
                    inference.LoadModel(existing);
            }

            return inference;
        });

        services.AddSingleton<KnowledgeGraphBridge>(sp =>
        {
            var graph = sp.GetRequiredService<KnowledgeGraph>();
            var llm = sp.GetService<IChatClient>();
            var logger = sp.GetService<ILogger<KnowledgeGraphBridge>>();
            return new KnowledgeGraphBridge(graph, llm, logger);
        });

        services.AddSingleton<MetaCognitiveLayer>(sp =>
        {
            var logger = sp.GetService<ILogger<MetaCognitiveLayer>>();
            return new MetaCognitiveLayer(logger);
        });

        services.AddSingleton<QueryPatternRouter>(sp =>
        {
            var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
            var logger = sp.GetService<ILogger<QueryPatternRouter>>();
            return new QueryPatternRouter(toolRegistry, logger);
        });

        services.AddSingleton<ResponseGroundingVerifier>();
        services.AddSingleton<L1PlanExecutor>();
        services.AddSingleton<ToolSelector>(sp =>
        {
            var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
            return new ToolSelector(toolRegistry);
        });
        services.AddSingleton<ModelHealthTracker>();
        services.AddSingleton<UnifiedQueryClassifier>();
        services.AddSingleton<PromptTemplateStore>(sp =>
        {
            var logger = sp.GetService<ILogger<PromptTemplateStore>>();
            var dir = Directory.GetCurrentDirectory();
            string? promptsDir = null;
            for (int i = 0; i < 5 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, "prompts");
                if (Directory.Exists(candidate)) { promptsDir = candidate; break; }
                dir = Path.GetDirectoryName(dir);
            }
            return new PromptTemplateStore(promptsDir, logger);
        });
        services.AddSingleton<BackgroundWorkQueue>(sp =>
        {
            var logger = sp.GetService<ILogger<BackgroundWorkQueue>>();
            return new BackgroundWorkQueue(capacity: 64, logger);
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

        // ONNX 训练流水线 HostedServices — 由 onnx_enabled 开关控制
        services.AddSingleton<IHostedService>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>();
            if (!opts.Value.AI.OnnxEnabled)
                return new NoOpHostedService();

            var config = sp.GetRequiredService<LocalLlmBootstrapConfig>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var engine = sp.GetRequiredService<IL1InferenceEngine>();
            var logger = sp.GetService<ILogger<LocalLlmBootstrapService>>();
            return new LocalLlmBootstrapService(config, httpClientFactory, engine, logger!);
        });

        services.AddSingleton<IHostedService>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>();
            if (!opts.Value.AI.OnnxEnabled)
                return new NoOpHostedService();

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

        services.AddSingleton<IHostedService>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>();
            if (!opts.Value.AI.OnnxEnabled)
                return new NoOpHostedService();

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

        services.AddSingleton<IHostedService>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>();
            if (!opts.Value.AI.OnnxEnabled)
                return new NoOpHostedService();

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

    private static string? ResolveApiKey(string providerName, string _)
    {
        var p = providerName.ToUpperInvariant();

        // 本地提供商无需 API Key
        if (p is "OLLAMA" or "LMSTUDIO" or "LM_STUDIO" or "VLLM" or "LLAMACPP" or "LLAMA_CPP" or "OPEN_WEBUI")
            return "";

        // 所有云端提供商的 API Key 均从环境变量读取
        var envVar = p switch
        {
            "DEEPSEEK"    => "DEEPSEEK_API_KEY",
            "OPENAI"      => "OPENAI_API_KEY",
            "ANTHROPIC"   => "ANTHROPIC_API_KEY",
            "GEMINI"      => "GEMINI_API_KEY",
            "SILICONFLOW" => "SILICONFLOW_API_KEY",
            "ALIYUN"      => "DASHSCOPE_API_KEY",
            "ZHIPU"       => "ZHIPU_API_KEY",
            "HUNYUAN"     => "HUNYUAN_API_KEY",
            "BAIDU"       => "BAIDU_API_KEY",
            "SPARK"       => "SPARK_API_KEY",
            "MOFANG"      => "MOFANG_API_KEY",
            "NVIDIA"      => "NVIDIA_API_KEY",
            "BAILING"     => "BAILING_API_KEY",
            "STEPFUN"     => "STEPFUN_API_KEY",
            "INTERNLM"    => "INTERNLM_API_KEY",
            "SENSETIME"   => "SENSETIME_API_KEY",
            "MODELSCOPE"  => "MODELSCOPE_API_KEY",
            "OPENROUTER"  => "OPENROUTER_API_KEY",
            "XIAOMI"      => "XIAOMI_API_KEY",
            "LONGCAT"     => "LONGCAT_API_KEY",
            "DMXAPI"      => "DMXAPI_API_KEY",
            "VOLCENGINE"  => "VOLCENGINE_API_KEY",
            "MOONSHOT"    => "MOONSHOT_API_KEY",
            "MINIMAX"     => "MINIMAX_API_KEY",
            "GROQ"        => "GROQ_API_KEY",
            "KIRO"        => "KIRO_API_KEY",
            "OPENCODE"    => "OPENCODE_API_KEY",
            _             => $"{p}_API_KEY"
        };

        return Environment.GetEnvironmentVariable(envVar);
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

internal sealed class NoOpHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
