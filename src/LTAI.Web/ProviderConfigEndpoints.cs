using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Core.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace LTAI.Web;

public static class ProviderConfigEndpoints
{
    public static void MapProviderConfigApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/providers");

        // List all configured providers with their status
        api.MapGet("/", (IOptions<LTAIOptions> opts) =>
        {
            var config = opts.Value;
            var providers = new List<object>();

            foreach (var (name, pc) in config.AI.Providers)
            {
                var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(
                    GetEnvVarName(name)));
                providers.Add(new
                {
                    name,
                    endpoint = pc.Endpoint,
                    model = pc.Model,
                    has_api_key = hasKey,
                    used_by = GetUsedByLayers(config, name)
                });
            }

            var allProviders = new ProviderRegistry().AllProviders
                .Select(p => new { name = p, configured = config.AI.Providers.ContainsKey(p), base_url = new ProviderRegistry().GetBaseUrl(p) });

            return Results.Ok(new { configured = providers, available = allProviders });
        });

        // Get L0/L1/L2 layer config
        api.MapGet("/layers", (IOptions<LTAIOptions> opts) =>
        {
            var config = opts.Value;
            return Results.Ok(new
            {
                l0 = new { config.AI.L0.Provider, config.AI.L0.Model },
                l1 = new { config.AI.L1.Provider, config.AI.L1.Model, config.AI.L1.Temperature },
                l2 = new { config.AI.L2.Provider, config.AI.L2.Model, config.AI.L2.Temperature },
                onnx_enabled = config.AI.OnnxEnabled,
                daily_budget = config.AI.DailyBudgetUsd
            });
        });

        // Set provider API key via POST
        api.MapPost("/key", async (HttpRequest request) =>
        {
            using var doc = await JsonDocument.ParseAsync(request.Body).ConfigureAwait(false);
            var provider = doc.RootElement.GetProperty("provider").GetString() ?? "";
            var apiKey = doc.RootElement.GetProperty("api_key").GetString() ?? "";

            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(apiKey))
                return Results.BadRequest(new { error = "provider and api_key required" });

            var envVar = GetEnvVarName(provider);
            Environment.SetEnvironmentVariable(envVar, apiKey, EnvironmentVariableTarget.User);

            return Results.Ok(new { provider, env_var = envVar, status = "saved" });
        });

        // Run setup wizard endpoint
        api.MapPost("/setup", async () =>
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var wizard = new InteractiveSetupWizard(configPath);
            await wizard.RunAsync().ConfigureAwait(false);
            return Results.Ok(new { status = "setup_complete" });
        });

        // Health with provider status
        api.MapGet("/health", () =>
        {
            var deepseekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            var aliyunKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
            var siliconflowKey = Environment.GetEnvironmentVariable("SILICONFLOW_API_KEY");

            return Results.Ok(new
            {
                status = "healthy",
                version = "0.51.0",
                timestamp = DateTime.UtcNow,
                providers = new
                {
                    deepseek = !string.IsNullOrEmpty(deepseekKey),
                    aliyun = !string.IsNullOrEmpty(aliyunKey),
                    siliconflow = !string.IsNullOrEmpty(siliconflowKey)
                }
            });
        });
    }

    private static string GetEnvVarName(string provider)
    {
        return provider.ToUpperInvariant() switch
        {
            "DEEPSEEK"    => "DEEPSEEK_API_KEY",
            "OPENAI"      => "OPENAI_API_KEY",
            "ANTHROPIC"   => "ANTHROPIC_API_KEY",
            "GEMINI"      => "GEMINI_API_KEY",
            "SILICONFLOW" => "SILICONFLOW_API_KEY",
            "ALIYUN"      => "DASHSCOPE_API_KEY",
            "ZHIPU"       => "ZHIPU_API_KEY",
            "HUNYUAN"     => "HUNYUAN_API_KEY",
            "BAIDU"       => "BAIDU_API_KEY",
            "SPARK"       => "SPARK_API_KEY",
            "MOFANG"      => "MOFANG_API_KEY",
            "NVIDIA"      => "NVIDIA_API_KEY",
            "BAILING"     => "BAILING_API_KEY",
            "STEPFUN"     => "STEPFUN_API_KEY",
            "INTERNLM"    => "INTERNLM_API_KEY",
            "SENSETIME"   => "SENSETIME_API_KEY",
            "MODELSCOPE"  => "MODELSCOPE_API_KEY",
            "OPENROUTER"  => "OPENROUTER_API_KEY",
            "XIAOMI"      => "XIAOMI_API_KEY",
            "LONGCAT"     => "LONGCAT_API_KEY",
            "DMXAPI"      => "DMXAPI_API_KEY",
            "VOLCENGINE"  => "VOLCENGINE_API_KEY",
            "MOONSHOT"    => "MOONSHOT_API_KEY",
            "MINIMAX"     => "MINIMAX_API_KEY",
            "GROQ"        => "GROQ_API_KEY",
            "KIRO"        => "KIRO_API_KEY",
            "OPENCODE"    => "OPENCODE_API_KEY",
            _             => $"{provider.ToUpperInvariant()}_API_KEY"
        };
    }

    private static string[] GetUsedByLayers(LTAIOptions config, string provider)
    {
        var layers = new List<string>();
        if (config.AI.L0.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)) layers.Add("L0");
        if (config.AI.L1.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)) layers.Add("L1");
        if (config.AI.L2.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)) layers.Add("L2");
        return layers.ToArray();
    }
}
