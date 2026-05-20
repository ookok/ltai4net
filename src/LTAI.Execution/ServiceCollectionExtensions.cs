using LTAI.Execution.Planning;
using LTAI.Execution.Quality;
using LTAI.Execution.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LTAI.Execution;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIExecution(this IServiceCollection services)
    {
        services.TryAddSingleton<SelfHealer>();

        services.TryAddSingleton(DiffusionPlanner.Instance);
        services.TryAddSingleton(GtsmPlanner.Instance);
        services.TryAddSingleton(TaskCheckpoint.Instance);
        services.TryAddSingleton(ThinkingEvolution.Instance);
        services.TryAddSingleton(CostAware.Instance);
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

        return services;
    }
}
