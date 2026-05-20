using LTAI.Core.System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Economy;

public static class EconomyServiceCollectionExtensions
{
    public static IServiceCollection AddEconomyAgentTraining(this IServiceCollection services)
    {
        services.AddSingleton<AgentGRPO>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var sessionResilience = SessionResilience.Instance;
            var traceReward = sp.GetRequiredService<TraceEfficiencyReward>();
            var opd = sp.GetRequiredService<OnPolicyDistillation>();
            var costEvaluator = sp.GetRequiredService<CostAwareEvaluator>();
            var logger = sp.GetService<ILogger<AgentGRPO>>();
            return new AgentGRPO(chatClient, sessionResilience, traceReward, opd, costEvaluator, logger);
        });

        services.AddSingleton<ExperienceReplayBuffer>();

        services.AddSingleton<BranchPolicyOptimizer>();

        services.AddSingleton<TraceEfficiencyReward>();

        services.AddSingleton<OnPolicyDistillation>();

        services.AddSingleton<CostAwareEvaluator>(sp =>
        {
            var traceReward = sp.GetRequiredService<TraceEfficiencyReward>();
            return new CostAwareEvaluator(traceReward);
        });

        services.AddSingleton<OldLogitSnapshotStore>();

        services.AddSingleton<OffPolicyCorrector>(sp =>
        {
            var snapshotStore = sp.GetRequiredService<OldLogitSnapshotStore>();
            return new OffPolicyCorrector(snapshotStore);
        });

        services.AddSingleton<PpoEwmaCorrector>(sp =>
        {
            var snapshotStore = sp.GetRequiredService<OldLogitSnapshotStore>();
            var offPolicyCorrector = sp.GetRequiredService<OffPolicyCorrector>();
            return new PpoEwmaCorrector(snapshotStore, offPolicyCorrector);
        });

        services.AddSingleton<PromptPool>();

        services.AddSingleton<HardwareProfiler>(sp =>
        {
            var config = ProfilingConfig.Default;
            return new HardwareProfiler(config);
        });

        services.AddSingleton<TieredEvaluator>(sp =>
        {
            var profiler = sp.GetRequiredService<HardwareProfiler>();
            var evaluator = new TieredEvaluator(profiler);

            evaluator.AddSecurityConstraint(new SecurityConstraint(
                "no_secrets", "Code must not contain hardcoded secrets",
                c => !c.Code.Contains("sk-") && !c.Code.Contains("api_key") && !c.Code.Contains("password")));

            evaluator.AddSecurityConstraint(new SecurityConstraint(
                "no_exec", "Code must not execute system commands",
                c => !c.Code.Contains("subprocess") && !c.Code.Contains("os.system")));

            evaluator.AddSecurityConstraint(new SecurityConstraint(
                "no_import_hijack", "Code must not hijack imports",
                c => !c.Code.Contains("sys.modules") && !c.Code.Contains("__import__")));

            return evaluator;
        });

        services.AddSingleton<EvolutionEngine>(sp =>
        {
            var config = EvolutionConfig.Default;
            var promptPool = sp.GetRequiredService<PromptPool>();
            var evaluator = sp.GetRequiredService<TieredEvaluator>();
            var chatClient = sp.GetRequiredService<IChatClient>();
            return new EvolutionEngine(config, promptPool, evaluator, chatClient);
        });

        return services;
    }
}
