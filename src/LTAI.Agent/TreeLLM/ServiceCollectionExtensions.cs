using LTAI.Agent.Adversarial;
using LTAI.Agent.Intelligence;
using LTAI.Agent.Resilience;
using LTAI.Agent.Routing;
using LTAI.Agent.Session;
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
        services.AddSingleton<DebugLoop>();
        services.AddSingleton<ErrorInterceptor>();
        services.AddSingleton<MultiModelConsensus>();
        services.AddSingleton<StreamingGrammarGuard>();
        services.AddSingleton<AutoTunerBridge>();
        return services;
    }
}
