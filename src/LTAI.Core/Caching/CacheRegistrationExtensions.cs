using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Core.Caching;

public static class CacheRegistrationExtensions
{
    public static IServiceCollection AddLTAICache(
        this IServiceCollection services)
    {
        services.AddSingleton<LTAICacheFactory>();
        return services;
    }

    public static IServiceCollection AddLTAICache<TKey, TValue>(
        this IServiceCollection services,
        string name,
        LTAICacheOptions? options = null)
        where TKey : notnull
    {
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<LTAICacheFactory>();
            return factory.GetOrCreate<TKey, TValue>(name, options);
        });
        return services;
    }
}
