using LTAI.AI;
using LTAI.Agent.Pipeline;
using LTAI.Core.Configuration;

namespace LTAI.Tests;

/// <summary>
/// Factory helpers for constructing P4/P5-refactored types in tests.
/// </summary>
internal static class TestHelper
{
    public static MultiProviderChatClient CreateRouter(LTAIOptions? options = null)
    {
        var opts = options ?? new LTAIOptions();
        var breaker = new CircuitBreakerManager(
            opts.Escalation.MaxFailuresBeforeCooldown,
            TimeSpan.FromSeconds(opts.Escalation.CooldownDurationSeconds));
        var cache = new ResponseCacheManager(256);
        var providers = new ProviderClientManager(
            opts.AI.DefaultProvider ?? "", breaker);
        return new MultiProviderChatClient(opts, providers, breaker, cache);
    }

    public static PipelineRunner CreateRunner(params IPipelineStep[] steps)
    {
        return new PipelineRunner(steps);
    }
}
