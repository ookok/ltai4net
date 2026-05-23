using System.Security.Cryptography;
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

var builder = WebApplication.CreateBuilder(args);

// Windows Service 部署: sc create LTAI binPath="dotnet LTAI.Host.dll"
// Linux systemd 部署: 创建 /etc/systemd/system/ltai.service

// 从 secrets JSON 文件加载所有密钥到环境变量
var secretsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "secrets_export.json");
SecretVault.LoadFromJsonFile(secretsPath);
SecretVault.LoadFromJsonFile(Path.Combine(AppContext.BaseDirectory, "secrets_export.json"));

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var isFirstRun = !File.Exists(configPath) || new FileInfo(configPath).Length < 50;

if (isFirstRun)
{
    Console.WriteLine("检测到首次运行，启动配置向导...");
    var setupWizard = new InteractiveSetupWizard(configPath);
    await setupWizard.RunAsync();
}

builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);

var ltaiSection = builder.Configuration.GetSection(LTAIOptions.SectionName);
builder.Services.Configure<LTAIOptions>(ltaiSection);
var ltaiOptions = ltaiSection.Get<LTAIOptions>() ?? new LTAIOptions();
var rateLimit = ltaiOptions.Web.RateLimitPerMinute > 0 ? ltaiOptions.Web.RateLimitPerMinute : 60;

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
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "ltai-.log"), rollingInterval: RollingInterval.Day));

builder.Services.AddLTAICore();
builder.Services.AddLTAIAgent();

var l0 = ltaiOptions.AI.L0;
var l0ProviderConfig = ltaiOptions.AI.Providers.TryGetValue(l0.Provider, out var l0p) ? l0p : null;
var l0ApiKey = l0ProviderConfig?.ApiKey ?? "";
if (string.IsNullOrEmpty(l0ApiKey))
{
    l0ApiKey = Environment.GetEnvironmentVariable($"{l0.Provider.ToUpperInvariant()}_API_KEY") ?? "";
}

var synapticDir = System.IO.Path.Combine(AppContext.BaseDirectory, "synaptic");
var onnxEmbeddingPath = System.IO.Path.Combine(synapticDir, "models", "embedding", "model.onnx");

var l0Endpoint = l0ProviderConfig != null ? $"{l0ProviderConfig.Endpoint.TrimEnd('/')}/v1" : null;
builder.Services.AddLTAIVectorAuto(
    apiEndpoint: l0Endpoint,
    apiKey: l0ApiKey,
    apiModel: l0.Model,
    onnxModelPath: onnxEmbeddingPath);
builder.Services.AddLTAIAI();
builder.Services.AddLTAIDocument(); builder.Services.AddLTAIDNA(); builder.Services.AddLTAIMemory();
builder.Services.AddLTAITreeLLM(); builder.Services.AddLTAIExecution(); builder.Services.AddLTAICapability();
builder.Services.AddLTAIEconomy(); builder.Services.AddLTAISandbox(); builder.Services.AddLTAIMetrics();
builder.Services.AddLTAIMultimodal(); builder.Services.AddLTAIMAF();
builder.Services.AddLTAINetwork();

// OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("LTAI.Host", serviceVersion: "7.0.0"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("LTAI.Agent.Mesh")
        .AddSource("LTAI.Agent")
        .AddSource("LTAI.Safety")
        .AddSource("LTAI.Router")
        .AddSource("LTAI.Agent.Execution")
        .AddSource("LTAI.Workflow")
        .AddSource("LTAI.Tool"))
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         .AddMeter("LTAI");
    });

var app = builder.Build();

// app.UseA2ABearerAuth();
app.UseLTAI();

using var scope = app.Services.CreateScope();
var sp = scope.ServiceProvider;
var logger = sp.GetRequiredService<ILogger<Program>>();

logger.LogInformation("LTAI Agent Mesh starting on {Port}", ltaiOptions.Web.Port);
logger.LogInformation("L1={L1Model} L2={L2Model} L0={L0Model}",
    ltaiOptions.AI.L1.Model, ltaiOptions.AI.L2.Model, ltaiOptions.AI.L0.Model);
logger.LogInformation("ONNX training: {Enabled}", ltaiOptions.AI.OnnxEnabled ? "enabled" : "disabled");

var l0Check = ltaiOptions.AI.L0;
var l0pc2 = ltaiOptions.AI.Providers.TryGetValue(l0Check.Provider, out var l02) ? l02 : null;
var l0KeySource = "none";
if (l0pc2 != null && !string.IsNullOrEmpty(l0pc2.ApiKey))
    l0KeySource = "appsettings";
else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable($"{l0Check.Provider.ToUpperInvariant()}_API_KEY")))
    l0KeySource = "env";

if (System.IO.File.Exists(onnxEmbeddingPath))
{
    logger.LogInformation("Embedding: ONNX local model ({Path})", onnxEmbeddingPath);
}
else
{
    if (l0KeySource != "none")
        logger.LogInformation("Embedding: {Provider} API ({Source}={Model})", l0Check.Provider, l0KeySource, l0Check.Model);
    else
        logger.LogInformation("Embedding: local backend (fallback)");
}

var token = Environment.GetEnvironmentVariable("A2A_BEARER_TOKEN") ?? "";
if (string.IsNullOrWhiteSpace(token))
{
    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    Environment.SetEnvironmentVariable("A2A_BEARER_TOKEN", token, EnvironmentVariableTarget.Process);
    logger.LogInformation("Generated new A2A Bearer token: {Token}...", token[..16]);
}

var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
sp.GetRequiredService<LTAI.Agent.Evolution.PluginRegistry>().Discover();
await LTAI.Agent.Tools.ToolRegistryExtensions.RegisterAllToolCategoriesAsync(toolRegistry, logger);
await sp.RegisterCodeActToolsAsync(toolRegistry);

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "6.2.0", timestamp = DateTime.UtcNow }));

await app.RunAsync();
