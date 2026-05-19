using LTAI.Cell.Lifecycle;
using LTAI.Cell.Training;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Cell;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICell(this IServiceCollection services)
    {
        services.AddSingleton(sp => CellTrainer.Instance);
        services.AddSingleton(sp => Mitosis.Instance);
        services.AddSingleton(sp => Distillation.Instance);
        services.AddSingleton(sp => DreamLearner.Instance);
        services.AddSingleton(sp => Regen.Instance);

        return services;
    }
}
