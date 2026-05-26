using LTAI.Core.Interfaces;
using System.Security.Cryptography;
using LTAI.AI.Interfaces;
using System.Threading.RateLimiting;
using LTAI.Core.Configuration;
using LTAI.Core.Messaging;
using LTAI.Agent;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Core;
using LTAI.Web;
using LTAI.DNA;
using LTAI.Tools;
using LTAI.Infra.Sandbox;
using LTAI.Planning.Metrics;
using LTAI.Infra.Multimodal;
using LTAI.Planning;
using LTAI.Planning.Planning;
using LTAI.Knowledge.Memory;
using LTAI.Knowledge.Document;
using LTAI.Knowledge.Vector;
using LTAI.Knowledge.Core;
using LTAI.Infra.Network;
using LTAI.Infra.Network.Interfaces;
using LTAI.Infra.Network.Bridge;
using LTAI.Economy;
using LTAI.Core.Setup;
using LTAI.Agent.Tools;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace LTAI.Host;

public static class EntryPoint
{
    public static async Task RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var secretsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "secrets_export.json");
        SecretVault.LoadFromJsonFile(secretsPath);
        SecretVault.LoadFromJsonFile(Path.Combine(OptionService.Get("paths.config") ?? AppContext.BaseDirectory, "secrets_export.json"));

        var configPath = Path.Combine(OptionService.Get("paths.config") ?? AppContext.BaseDirectory, "appsettings.json");
        var status = FirstRunDetector.Check(configPath);
        if (status.IsFirstRun)
        {
            FirstRunDetector.PrintDiagnostics(status);
            Console.WriteLine("检测到未配置，启动配置向导...");
            await new InteractiveSetupWizard(configPath).RunAsync().ConfigureAwait(false);
        }

        builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);

        var ltaiSection = builder.Configuration.GetSection(LTAIOptions.SectionName);
        builder.Services.Configure<LTAIOptions>(ltaiSection);
        var ltaiOptions = ltaiSection.Get<LTAIOptions>() ?? new LTAIOptions();
        var rateLimit = ltaiOptions.Web.RateLimitPerMinute > 0 ? ltaiOptions.Web.RateLimitPerMinute : 60;

        if (ltaiOptions.AI.Providers.Count == 0)
        {
            ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig
            {
                Endpoint = OptionService.Get("deepseek.endpoint") ?? "https://api.deepseek.com",
                Model = OptionService.Get("deepseek.model") ?? "deepseek-v4-pro"
            };
            ltaiOptions.AI.Providers["deepseek-fast"] = new ProviderConfig
            {
                Endpoint = OptionService.Get("deepseek.fast.endpoint") ?? "https://api.deepseek.com",
                Model = OptionService.Get("deepseek.fast.model") ?? "deepseek-v4-flash"
            };
        }

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddFixedWindowLimiter("LTAI", config =>
            {
                config.PermitLimit = rateLimit;
                config.Window = TimeSpan.FromMinutes(1);
            });
        });

        builder.Host.UseSerilog((ctx, lc) => lc
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(OptionService.Get("paths.logs") ?? Path.Combine(AppContext.BaseDirectory, "logs"), "ltai-.log"), rollingInterval: RollingInterval.Day));

        builder.Services.AddLTAICore();
        builder.Services.AddLTAIAgent();

        var l0 = ltaiOptions.AI.L0;
        var l0ProviderConfig = ltaiOptions.AI.Providers.TryGetValue(l0.Provider, out var l0p) ? l0p : null;
        var l0ApiKey = l0ProviderConfig?.ApiKey ?? "";
        if (string.IsNullOrEmpty(l0ApiKey))
            l0ApiKey = OptionService.Get($"{l0.Provider.ToUpperInvariant()}_API_KEY") ?? "";

        var onnxEmbeddingPath = System.IO.Path.Combine(OptionService.Get("paths.models") ?? System.IO.Path.Combine(AppContext.BaseDirectory, "models"), "l0", "model.onnx");
        var l0Endpoint = l0ProviderConfig != null ? $"{l0ProviderConfig.Endpoint.TrimEnd('/')}/v1" : null;
        builder.Services.AddLTAIVectorAuto(
            apiEndpoint: l0Endpoint, apiKey: l0ApiKey, apiModel: l0.Model, onnxModelPath: onnxEmbeddingPath);
        builder.Services.AddLTAIAI();
        builder.Services.AddLTAIDocument(); builder.Services.AddLTAIDNA(); builder.Services.AddLTAIMemory();
        builder.Services.AddLTAITreeLLM(); builder.Services.AddLTAIExecution(); builder.Services.AddLTAICapability();
        builder.Services.AddLTAIEconomy(); builder.Services.AddLTAISandbox(); builder.Services.AddLTAIMetrics();
        builder.Services.AddLTAIMultimodal(); builder.Services.AddLTAIMAF();
        builder.Services.AddLTAINetwork();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("LTAI.Host", serviceVersion: "0.51.0"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("LTAI.Agent.Mesh").AddSource("LTAI.Agent").AddSource("LTAI.Safety")
                .AddSource("LTAI.Router").AddSource("LTAI.Agent.Execution").AddSource("LTAI.Workflow").AddSource("LTAI.Tool"))
            .WithMetrics(m => { m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter("LTAI"); });

        var app = builder.Build();
        app.UseLTAIExceptionHandler();
        app.UseLTAI();

        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("LTAI Agent Mesh starting on {Port}", ltaiOptions.Web.Port);
        logger.LogInformation("L1={L1} L2={L2} L0={L0}", ltaiOptions.AI.L1.Model, ltaiOptions.AI.L2.Model, ltaiOptions.AI.L0.Model);
        logger.LogInformation("ONNX training: {Enabled}", ltaiOptions.AI.OnnxEnabled ? "enabled" : "disabled");

        var token = OptionService.Get("A2A_BEARER_TOKEN") ?? "";
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Environment.SetEnvironmentVariable("A2A_BEARER_TOKEN", token, EnvironmentVariableTarget.Process);
            logger.LogInformation("Generated new A2A Bearer token: {Token}...", token[..4]);
        }

        var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
        sp.GetRequiredService<LTAI.Agent.Evolution.PluginRegistry>().Discover();
        await LTAI.Agent.Tools.ToolRegistryExtensions.RegisterAllToolCategoriesAsync(toolRegistry, logger).ConfigureAwait(false);
        await sp.RegisterCodeActToolsAsync(toolRegistry).ConfigureAwait(false);
        await sp.RegisterMarkdownToolsAsync(toolRegistry).ConfigureAwait(false);

        var lts = sp.GetRequiredService<ILivingTreeSystem>();
        await lts.InitializeAsync().ConfigureAwait(false);

        var skillRegistry = sp.GetRequiredService<LTAI.Agent.Skills.SkillRegistry>();
        await skillRegistry.LoadAllAsync().ConfigureAwait(false);
        logger.LogInformation("LTAI Agent Mesh initialized ({SkillCount} skills loaded)",
            skillRegistry.All.Count);

        app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.51.0", timestamp = DateTime.UtcNow }));

        await app.RunAsync().ConfigureAwait(false);
    }
}

internal sealed class HostEntryPointAdapter : ILTAIEntryPoint
{
    private static readonly HashSet<string> _modes = new(StringComparer.OrdinalIgnoreCase) { "host", "serve" };
    public bool CanHandle(string command) => _modes.Contains(command);
    public Task RunAsync(string[] args) => EntryPoint.RunAsync(args);
}

public static class HostEntryPointRegistration
{
    static HostEntryPointRegistration()
    {
        LTAIEntryPointRegistry.Register("host", new HostEntryPointAdapter());
        LTAIEntryPointRegistry.Register("serve", new HostEntryPointAdapter());
    }

    public static void Initialize() { }
}
