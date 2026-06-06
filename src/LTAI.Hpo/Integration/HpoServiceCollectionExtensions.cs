using System;
using LTAI.Hpo.Dashboard;
using LTAI.Hpo.Pruners;
using LTAI.Hpo.Samplers;
using LTAI.Hpo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LTAI.Hpo.Integration;

/// <summary>
/// DI extension methods for registering HPO services.
/// </summary>
public static class HpoServiceCollectionExtensions
{
    /// <summary>
    /// Register HPO services with a default SQLite store.
    /// </summary>
    /// <param name="sqliteConnectionString">SQLite connection string for trial storage.
    /// If null, studies run in-memory only (not persisted across restarts).</param>
    public static IServiceCollection AddLtaiHpo(this IServiceCollection services,
        string? sqliteConnectionString = null)
    {
        if (sqliteConnectionString != null)
        {
            services.AddSingleton<IStudyStore>(_ => new SqliteStudyStore(sqliteConnectionString));
        }

        services.AddSingleton<HpoDashboard>();
        services.TryAddTransient<RandomSampler>();
        services.TryAddTransient<TpeSampler>();
        services.TryAddTransient(typeof(MedianPruner));
        services.TryAddTransient(typeof(ThresholdPruner));

        return services;
    }

    /// <summary>Create a study with DI-resolved services.</summary>
    public static Study CreateStudy(this IServiceProvider sp,
        string name,
        ISampler sampler,
        StudyDirection direction = StudyDirection.Minimize)
    {
        var store = sp.GetService<IStudyStore>();
        var dashboard = sp.GetRequiredService<HpoDashboard>();

        var study = new Study(name, sampler, store, direction: direction);
        dashboard.Track(name, study);
        return study;
    }

    /// <summary>Create a study with TPE sampler (Optuna default).</summary>
    public static Study CreateTpeStudy(this IServiceProvider sp,
        string name,
        int? seed = null,
        StudyDirection direction = StudyDirection.Minimize)
    {
        return sp.CreateStudy(name, new TpeSampler(seed), direction);
    }
}