using LTAI.Planning.HTN;
using LTAI.Planning.Planning;
using LTAI.Planning.Quality;
using LTAI.Planning.Session;
using LTAI.Planning.Trace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LTAI.Planning;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIExecution(this IServiceCollection services)
    {
        services.TryAddSingleton<SelfHealer>();

        services.TryAddSingleton(DiffusionPlanner.Instance);
        services.TryAddSingleton(GtsmPlanner.Instance);
        services.TryAddSingleton(TaskCheckpoint.Instance);
        services.TryAddSingleton<CostAware>();
        services.TryAddSingleton(CoFEECognitiveEngine.Instance);
        services.TryAddSingleton(FitnessLandscape.Instance);
        services.TryAddSingleton(RankMonitor.Instance);
        services.TryAddSingleton(ThompsonDelegator.Instance);
        services.TryAddSingleton(Clarifier.Instance);
        services.TryAddSingleton(AutoSkillResolver.Instance);
        services.TryAddSingleton(SessionManager.Instance);
        services.TryAddSingleton(SideGit.Instance);
        services.TryAddSingleton(TerminalCompressor.Instance);
        services.TryAddSingleton(GlobalRulePool.Instance);

        services.TryAddSingleton<HTNPlanner>();
        services.TryAddSingleton<TraceCollector>();

        return services;
    }
}
