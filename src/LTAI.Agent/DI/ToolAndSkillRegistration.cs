using LTAI.AI;
using LTAI.Agent.Indexing;
using LTAI.Agent.Learning;
using LTAI.Agent.SeedER;
using LTAI.Agent.Tools;
using LTAI.Agent.CodeAnalysis;
using LTAI.Agent.Vector;
using LTAI.Agent.Execution;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Indexing services, knowledge tools, code chunk index,
    /// seedER, skill evolution engine, summarizer tools.
    /// </summary>
    static IServiceCollection AddLTAIAgentIndexingAndTools(this IServiceCollection services)
    {
        // Document indexing pipeline
        services.AddSingleton<DocumentPageAnnotator>(sp =>
        {
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DocumentPageAnnotator>();
            return new DocumentPageAnnotator(l3, logger);
        });
        services.AddSingleton<DocumentIndexer>();
        services.AddSingleton<IndexQueueWorker>(sp =>
        {
            var indexer = sp.GetRequiredService<DocumentIndexer>();
            var queue = sp.GetRequiredService<Tasks.TaskQueue>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<IndexQueueWorker>();
            return new IndexQueueWorker(indexer, queue, logger!);
        });
        services.AddSingleton<RetryQueueWorker>(sp =>
        {
            var client = sp.GetRequiredService<MultiProviderChatClient>();
            var queue = sp.GetRequiredService<Tasks.TaskQueue>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<RetryQueueWorker>();
            return new RetryQueueWorker(client, queue, logger!);
        });

        // Knowledge extraction
        services.AddSingleton<KnowledgeExtractor>(sp =>
        {
            var kg = sp.GetRequiredService<KgStore>();
            var llm = sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<KnowledgeExtractor>();
            return new KnowledgeExtractor(kg, llm, logger);
        });
        services.AddSingleton<KnowledgeQualityScorer>();
        services.AddSingleton<ProvenanceTracker>();
        services.AddSingleton<ProvenanceProvider>();
        services.AddSingleton<KnowledgeAssetTool>();
        services.AddSingleton<TaskQueueTool>(sp =>
            new TaskQueueTool(
                sp.GetRequiredService<Tasks.TaskQueue>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<TaskQueueTool>()));

        // Code analysis
        services.AddSingleton<CodeChunkIndex>(sp =>
        {
            var store = sp.GetRequiredService<KgStore>();
            var parser = new TreeSitterParser(
                sp.GetService<ILogger<TreeSitterParser>>());
            return new CodeChunkIndex(store, parser,
                sp.GetService<EmbeddingClient>(),
                sp.GetService<ILogger<CodeChunkIndex>>(),
                Directory.GetCurrentDirectory());
        });
        services.AddSingleton<FailureMiner>();

        // User-facing tools
        services.AddSingleton<QuestionService>();
        services.AddSingleton<QuestionTool>();
        services.AddSingleton<ClusterSummarizer>(sp =>
            new ClusterSummarizer(
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<ClusterSummarizer>()));
        services.AddSingleton<DeepenSearchTool>(sp =>
            new DeepenSearchTool(
                sp.GetRequiredService<Vector.KbGraph>(),
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<DeepenSearchTool>()));

        // SeedER
        services.AddSingleton<PathExplorer>(sp =>
            new PathExplorer(sp.GetRequiredService<KgStore>()));
        services.AddSingleton<SeedER.SeedER>(sp =>
            new SeedER.SeedER(
                sp.GetRequiredService<KgStore>(),
                sp.GetRequiredService<PathExplorer>(),
                sp.GetService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<SeedER.SeedER>()));
        services.AddSingleton<SeedERTool>(sp =>
            new SeedERTool(
                sp.GetRequiredService<SeedER.SeedER>(),
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<SeedERTool>()));

        // Model switch bridge
        services.AddSingleton<LocalEmbedderModelSwitchNotifier>(sp =>
            new LocalEmbedderModelSwitchNotifier(sp.GetService<LocalEmbedder>()));

        // Skill evolution engine
        services.AddSingleton<SkillValidationGate>(sp =>
        {
            var judge = sp.GetKeyedService<IChatClient>("steer") ?? sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillValidationGate>();
            return new SkillValidationGate(judge, logger, ResolveSkillsDir());
        });
        services.AddSingleton<SkillEditBudget>(sp =>
            new SkillEditBudget(ResolveSkillsDir()));
        services.AddSingleton<SkillRejectedBuffer>(sp =>
            new SkillRejectedBuffer(ResolveSkillsDir()));
        services.AddSingleton<SkillEvalBenchmark>(sp =>
        {
            var gate = sp.GetRequiredService<SkillValidationGate>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillEvalBenchmark>();
            return new SkillEvalBenchmark(gate, logger, ResolveSkillsDir());
        });
        services.AddSingleton<SkillEvolutionEngine>(sp =>
        {
            var llm = sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillEvolutionEngine>();
            return new SkillEvolutionEngine(llm, logger, ResolveSkillsDir(),
                validationGate: sp.GetService<SkillValidationGate>(),
                editBudget: sp.GetService<SkillEditBudget>(),
                rejectedBuffer: sp.GetService<SkillRejectedBuffer>(),
                evalBenchmark: sp.GetService<SkillEvalBenchmark>());
        });

        // P6: Bounded session state stores (replace static ConcurrentDictionary instances)
        services.AddSingleton<PlanStore>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<Long2ShortTracker>();

        return services;
    }
}
