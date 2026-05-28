using LTAI.Agent.Adversarial;
using LTAI.Agent.Intelligence;
using LTAI.Agent.Resilience;
using LTAI.Agent.Routing;
using LTAI.Agent.Session;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Agent;

public static class TreeLLMServiceCollectionExtensions
{
    public static IServiceCollection AddLTAITreeLLM(this IServiceCollection services)
    {
        services.AddSingleton<ModelRegistry>();
        services.AddSingleton<BudgetRouter>();
        services.AddSingleton<LatencyOracle>();
        services.AddSingleton<CompetitiveEliminator>();
        services.AddSingleton<ContinuousBenchmark>();
        services.AddSingleton<QueryClassifier>();
        services.AddSingleton<HealthPredictor>();
        services.AddSingleton<ReasoningBudgetEngine>();
        services.AddSingleton<SessionBinding>();
        services.AddSingleton<SessionCompressor>();
        services.AddSingleton<CrossSessionBridge>();
        services.AddSingleton<ConnectionPool>();
        services.AddSingleton<ContinuousConsciousness>();
        services.AddSingleton<FreeModelPool>();
        services.AddSingleton<SegmentedKVCompressor>();
        services.AddSingleton<DataValueDensity>();
        services.AddSingleton<SelfImprover>();
        services.AddSingleton<AdversarialGate>();
        services.AddSingleton<TokenCircuitBreaker>();
        services.AddSingleton<FluidCollective>();
        services.AddSingleton<DebugLoop>(sp =>
        {
            var chatClient = sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>();
            var correctionMemory = sp.GetService<LTAI.AI.Governors.CorrectionMemory>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<DebugLoop>>();
            var harnessProfile = sp.GetService<LTAI.Core.Configuration.HarnessProfile>();
            var workspace = OptionService.Get("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory();
            var crossRunStore = sp.GetService<LTAI.AI.Governors.ICrossRunEvolutionStore>();
            Action<LTAI.Agent.Models.DebugSession>? onFixed = crossRunStore != null
                ? (session) =>
                {
                    var lesson = new LTAI.AI.Governors.EvolutionLesson
                    {
                        Category = "QualityRegression",
                        Severity = 0.6f,
                        Summary = $"DebugLoop fixed: {session.Target}",
                        Mitigation = $"Auto-fix applied after {session.Attempts.Count} attempt(s)",
                        SourceStage = "debug_loop",
                        SourceRun = session.Id
                    };
                    crossRunStore.RecordLesson(lesson);
                }
                : null;
            return new DebugLoop(chatClient, correctionMemory, logger, null, harnessProfile, workspace,
                onFixed: onFixed);
        });
        services.AddSingleton<ErrorInterceptor>();
        services.AddSingleton<MultiModelConsensus>();
        services.AddSingleton<StreamingGrammarGuard>();
        services.AddSingleton<AutoTunerBridge>();
        return services;
    }
}
