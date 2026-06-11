using LTAI.AI;
using LTAI.Agent.Prompts;
using LTAI.Core.Configuration;
using LTAI.Core.I18n;
using LTAI.Core.Safety;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    private static readonly string SafetyPrompt = LoadSafetyPrompt();

    private static string LoadSafetyPrompt()
    {
        var lang = Locale.IsChinese ? "zh" : "en";
        var filePrompt = PromptLoader.Load($"safety-{lang}");
        return !string.IsNullOrEmpty(filePrompt) ? filePrompt : SafetyPrompts.DefaultSystemPrompt;
    }

    internal static SafetyCoordinator? BuildSafetyCoordinator(IServiceProvider sp, LTAIOptions opts, ILogger log, string name)
    {
        SafetyCoordinator? safety = null;
        if (opts.AI.SkipSafetyChecks) return null;

        var steerLlm = sp.GetKeyedService<IChatClient>("steer");
        IChatClient? safetyClient = null;

        if (steerLlm != null)
        {
            safetyClient = steerLlm;
        }
        else
        {
            var safetyModel = !string.IsNullOrEmpty(opts.AI.Model)
                ? opts.AI.Model
                : opts.AI.L1?.Model;

            if (string.IsNullOrEmpty(safetyModel))
            {
                // Fallback: use ProviderRegistry to find the active provider's default model
                var registry = sp.GetService<ProviderRegistry>();
                var activeProvider = registry?.ActiveProviders.FirstOrDefault();
                safetyModel = activeProvider?.Models.FirstOrDefault()?.ShortId;
            }

            if (string.IsNullOrEmpty(safetyModel))
            {
                log?.LogWarning("Safety agent: no model, skipping for agent '{Name}'", name);
                return null;
            }

            var safetyKey = opts.AI.ApiKeyEnv != null ? SecretManager.Get(opts.AI.ApiKeyEnv) ?? "" : "";
            if (string.IsNullOrEmpty(safetyKey))
            {
                log?.LogWarning("Safety agent: no API key ({Env}), skipping for agent '{Name}'", opts.AI.ApiKeyEnv ?? "?", name);
                return null;
            }

            safetyClient = OpenAIChatClientFactory.Create("https://api.deepseek.com/v1", safetyModel, safetyKey);
        }

        if (safetyClient != null)
        {
            safety = new SafetyCoordinator(safetyClient,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SafetyCoordinator>(),
                safetyPrompt: SafetyPrompt);
        }

        return safety;
    }
}
