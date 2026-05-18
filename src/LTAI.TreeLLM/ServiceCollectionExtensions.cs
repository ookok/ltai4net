using LTAI.TreeLLM.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.TreeLLM;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAITreeLLM(this IServiceCollection services)
    {
        services.AddSingleton<ModelRegistry>();
        services.AddSingleton<BudgetRouter>();
        services.AddSingleton<LatencyOracle>();
        services.AddSingleton<CompetitiveEliminator>();
        services.AddSingleton<ContinuousBenchmark>();
        services.AddSingleton<QueryClassifier>();
        return services;
    }
}
