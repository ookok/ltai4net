using LTAI.AI.Governors;
using LTAI.AI.Providers;
using LTAI.Capability.Skills;
using LTAI.Vector.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        services.AddSingleton<ProviderEngine>();
        services.AddSingleton<ProviderFanOutRace>();

        services.AddSingleton<IChatClient>(sp =>
        {
            var engine = sp.GetRequiredService<ProviderEngine>();
            var pipeline = new ChatClientBuilder(engine)
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
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
            var graph = new KnowledgeGraph(logger!);
            var graphPath = System.IO.Path.Combine(AppContext.BaseDirectory, ".livingtree", "knowledge_graph.json");
            if (System.IO.File.Exists(graphPath))
                graph.LoadFromDisk(graphPath);
            return graph;
        });

        var synapticDir = System.IO.Path.Combine(AppContext.BaseDirectory, "synaptic");
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
        services.AddSingleton<UserContextTracker>();
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
            return new CellAIRegistry(answerStore, trainer, memory, logger);
        });

        services.AddSingleton<L1L2DuplexRouter>(sp =>
        {
            var inference = sp.GetRequiredService<SynapticInference>();
            var memory = sp.GetRequiredService<SynapticMemory>();
            var graphBridge = sp.GetRequiredService<KnowledgeGraphBridge>();
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
            return new L1L2DuplexRouter(inference, memory, graphBridge, metaCognition, skillTree, cache, ruleExtractor, costRouter, knowledge, classifier, cellRegistry, llm, logger);
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
            var logger = sp.GetService<ILogger<DreamCycle>>();
            return new DreamCycle(memory, graphBridge, skillTree, metaCognition, logger);
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
        services.AddSingleton<CapabilityGovernor>();
        services.AddSingleton<StorageGovernor>();
        services.AddSingleton<OutputGovernor>();
        services.AddSingleton<CommunicationGovernor>();
        services.AddSingleton<TaskGovernor>();
        services.AddSingleton<SelfGovernor>();
        services.AddSingleton<EvolutionGovernor>();
        services.AddSingleton<SystemGuardian>();
        services.AddSingleton<LivingTreeSystem>();

        return services;
    }
}
