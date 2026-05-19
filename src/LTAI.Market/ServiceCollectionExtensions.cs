using LTAI.Market.Intel;
using LTAI.Market.Opportunity;
using LTAI.Market.Profiling;
using LTAI.Market.Revenue;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Market;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMarket(this IServiceCollection services)
    {
        services.AddSingleton(UserProfileEngine.Instance);
        services.AddSingleton(OpportunityScorer.Instance);
        services.AddSingleton(MarketTrendAnalyzer.Instance);
        services.AddSingleton(BiddingAssistant.Instance);
        services.AddSingleton<RevenueEngine>(_ => RevenueEngine.Instance);
        services.AddSingleton(SelfInvestmentEngine.Instance);
        services.AddSingleton(ListedCompanyIntel.Instance);

        return services;
    }
}
