using LTAI.Core.Messaging;
using LTAI.TreeLLM.Session;
using LTAI.Vector.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Prompting;

public static class TreeLLMServiceCollectionExtensions
{
    public static IServiceCollection AddTreeLLMPrompting(this IServiceCollection services)
    {
        services.AddSingleton<PromptBuilder>();

        services.AddSingleton<RagPipeline>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<RagPipeline>>();
            return new RagPipeline(chatClient, agenticRAG, promptBuilder, logger);
        });

        services.AddSingleton<SessionRagService>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<SessionRagService>>();
            return new SessionRagService(chatClient, agenticRAG, structMemory, promptBuilder, logger);
        });

        services.AddSingleton<NestedRagLoop>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<NestedRagLoop>>();
            return new NestedRagLoop(agenticRAG, orchestrator: null, promptBuilder, logger);
        });

        services.AddSingleton<MoECompressionBridge>();

        services.AddSingleton<ContinuousLearningLoop>();

        services.AddSingleton<MctsAgentReasoner>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var logger = sp.GetService<ILogger<MctsAgentReasoner>>();
            return new MctsAgentReasoner(chatClient, promptBuilder, agenticRAG, logger);
        });

        services.AddSingleton<SelfRefinementLoop>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<SelfRefinementLoop>>();
            return new SelfRefinementLoop(chatClient, agenticRAG, promptBuilder, logger);
        });

        services.AddSingleton<ParallelReasoningGraph>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<ParallelReasoningGraph>>();
            return new ParallelReasoningGraph(chatClient, agenticRAG, promptBuilder, logger);
        });

        services.AddSingleton<SelfDistillPipeline>(sp =>
        {
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<SelfDistillPipeline>>();
            return new SelfDistillPipeline(promptBuilder, logger);
        });

        services.AddSingleton<ReversePplCurriculum>();

        services.AddSingleton<MultiToolDispatch>(sp =>
        {
            var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
            return new MultiToolDispatch(toolRegistry);
        });

        services.AddSingleton<OnlineMemoryState>();

        services.AddSingleton<SegmentWriteStrategies>(sp =>
        {
            var memoryState = sp.GetRequiredService<OnlineMemoryState>();
            return new SegmentWriteStrategies(memoryState);
        });

        services.AddSingleton<DeltaMemAdapter>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var memoryState = sp.GetRequiredService<OnlineMemoryState>();
            var logger = sp.GetService<ILogger<DeltaMemAdapter>>();
            return new DeltaMemAdapter(chatClient, memoryState, logger);
        });

        services.AddSingleton<PersonalModelEmulator>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            return new PersonalModelEmulator(agenticRAG, structMemory, promptBuilder);
        });

        services.AddSingleton<ContextProviderRouter>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            return new ContextProviderRouter(agenticRAG, promptBuilder);
        });

        services.AddSingleton<DualPerspectiveMemory>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            return new DualPerspectiveMemory(agenticRAG, structMemory);
        });

        services.AddSingleton<OnPolicyDataEvolver>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var logger = sp.GetService<ILogger<OnPolicyDataEvolver>>();
            return new OnPolicyDataEvolver(chatClient, agenticRAG, logger);
        });

        services.AddSingleton<EntailmentAligner>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            return new EntailmentAligner(agenticRAG);
        });

        services.AddSingleton<DisclosurePolicyLearner>(sp =>
        {
            var aligner = sp.GetRequiredService<EntailmentAligner>();
            return new DisclosurePolicyLearner(aligner);
        });

        return services;
    }

    public static IServiceCollection AddTreeLLMPrompting(
        this IServiceCollection services,
        IChatClient chatClient)
    {
        services.AddSingleton<PromptBuilder>();

        services.AddSingleton<RagPipeline>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<RagPipeline>>();
            return new RagPipeline(chatClient, agenticRAG, promptBuilder, logger);
        });

        services.AddSingleton<SessionRagService>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<SessionRagService>>();
            return new SessionRagService(chatClient, agenticRAG, structMemory, promptBuilder, logger);
        });

        services.AddSingleton<NestedRagLoop>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<NestedRagLoop>>();
            return new NestedRagLoop(agenticRAG, orchestrator: null, promptBuilder, logger);
        });

        services.AddSingleton<MoECompressionBridge>();

        services.AddSingleton<ContinuousLearningLoop>();

        services.AddSingleton<MctsAgentReasoner>(sp =>
        {
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var logger = sp.GetService<ILogger<MctsAgentReasoner>>();
            return new MctsAgentReasoner(chatClient, promptBuilder, agenticRAG, logger);
        });

        services.AddSingleton<SelfRefinementLoop>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<SelfRefinementLoop>>();
            return new SelfRefinementLoop(chatClient, agenticRAG, promptBuilder, logger);
        });

        services.AddSingleton<ParallelReasoningGraph>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<ParallelReasoningGraph>>();
            return new ParallelReasoningGraph(chatClient, agenticRAG, promptBuilder, logger);
        });

        services.AddSingleton<SelfDistillPipeline>(sp =>
        {
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            var logger = sp.GetService<ILogger<SelfDistillPipeline>>();
            return new SelfDistillPipeline(promptBuilder, logger);
        });

        services.AddSingleton<ReversePplCurriculum>();

        services.AddSingleton<MultiToolDispatch>(sp =>
        {
            var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
            return new MultiToolDispatch(toolRegistry);
        });

        services.AddSingleton<OnlineMemoryState>();

        services.AddSingleton<SegmentWriteStrategies>(sp =>
        {
            var memoryState = sp.GetRequiredService<OnlineMemoryState>();
            return new SegmentWriteStrategies(memoryState);
        });

        services.AddSingleton<DeltaMemAdapter>(sp =>
        {
            var memoryState = sp.GetRequiredService<OnlineMemoryState>();
            var logger = sp.GetService<ILogger<DeltaMemAdapter>>();
            return new DeltaMemAdapter(chatClient, memoryState, logger);
        });

        services.AddSingleton<PersonalModelEmulator>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            return new PersonalModelEmulator(agenticRAG, structMemory, promptBuilder);
        });

        services.AddSingleton<ContextProviderRouter>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var promptBuilder = sp.GetRequiredService<PromptBuilder>();
            return new ContextProviderRouter(agenticRAG, promptBuilder);
        });

        services.AddSingleton<DualPerspectiveMemory>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var structMemory = sp.GetRequiredService<StructMemory>();
            return new DualPerspectiveMemory(agenticRAG, structMemory);
        });

        services.AddSingleton<OnPolicyDataEvolver>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            var logger = sp.GetService<ILogger<OnPolicyDataEvolver>>();
            return new OnPolicyDataEvolver(chatClient, agenticRAG, logger);
        });

        services.AddSingleton<EntailmentAligner>(sp =>
        {
            var agenticRAG = sp.GetRequiredService<AgenticRAG>();
            return new EntailmentAligner(agenticRAG);
        });

        services.AddSingleton<DisclosurePolicyLearner>(sp =>
        {
            var aligner = sp.GetRequiredService<EntailmentAligner>();
            return new DisclosurePolicyLearner(aligner);
        });

        return services;
    }
}
