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
using LTAI.Knowledge.Memory;
using LTAI.Knowledge.Document;
using LTAI.Knowledge.Vector;
using LTAI.Infra.Browser;
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

builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, config) => config.ReadFrom.Configuration(context.Configuration));

var resourceBuilder = ResourceBuilder.CreateDefault().AddService("LTAI", "5.5.0-net10");
builder.Logging.AddOpenTelemetry(o => { o.SetResourceBuilder(resourceBuilder); o.IncludeFormattedMessage = true; });
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddSource("LTAI.AI", "LTAI.TreeLLM", "LTAI.Execution", "Microsoft.Agents.AI").AddConsoleExporter())
    .WithMetrics(m => m.SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddMeter("Microsoft.Agents.AI").AddConsoleExporter());

builder.Services.AddHealthChecks();

builder.Services.AddLTAICore();
var l0 = ltaiOptions.AI.L0;
var l0ProviderConfig = ltaiOptions.AI.Providers.TryGetValue(l0.Provider, out var l0p) ? l0p : null;
var l0ApiKey = l0ProviderConfig?.ApiKey ?? "";
if (string.IsNullOrEmpty(l0ApiKey))
{
    var secretKey = $"{l0.Provider}_api_key";
    l0ApiKey = SecretVault.Instance.Get(secretKey);
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

var app = builder.Build();

// app.UseA2ABearerAuth();
app.UseLTAI();

app.MapMAFEndpoints(); app.MapDNAEndpoints(); app.MapCapabilityEndpoints();
app.MapSandboxEndpoints(); app.MapMultimodalEndpoints(); app.MapExecutionEndpoints();
app.UseLTAIMetrics(); app.MapMCPEndpoints(); app.MapAHEEndpoints();
app.MapNetworkEndpoints();
app.MapA2AHttpJson("LTAI", "/a2a/livingtree");

app.UseSerilogRequestLogging();

var sp = app.Services;
var config = AppConfiguration.Load();
var logger = sp.GetRequiredService<ILogger<Program>>();

var l0Check = ltaiOptions.AI.L0;
var l0pc2 = ltaiOptions.AI.Providers.TryGetValue(l0Check.Provider, out var l0pc3) ? l0pc3 : null;
var l0KeySource = "local";
if (l0pc2 != null && !string.IsNullOrEmpty(l0pc2.ApiKey))
    l0KeySource = "appsettings";
else if (!string.IsNullOrEmpty(SecretVault.Instance.Get($"{l0Check.Provider}_api_key")))
    l0KeySource = "secrets.enc";

var onnxEmbeddingPath2 = System.IO.Path.Combine(AppContext.BaseDirectory, "synaptic", "models", "embedding", "model.onnx");

if (l0Check.IsConfigured && l0KeySource != "local")
{
    logger.LogInformation("Embedding: API ({Provider}/{Model} via {Endpoint}, key from {Source})", l0Check.Provider, l0Check.Model, l0pc2?.Endpoint, l0KeySource);
}
else if (System.IO.File.Exists(onnxEmbeddingPath2))
{
    logger.LogInformation("Embedding: ONNX local model ({Path})", onnxEmbeddingPath2);
}
else
{
    logger.LogInformation("Embedding: local backend (fallback)");
}

var token = SecretVault.Instance.Get("a2a_bearer_token");
if (string.IsNullOrWhiteSpace(token))
{
    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    SecretVault.Instance.Set("a2a_bearer_token", token);
    logger.LogInformation("Generated new A2A Bearer token: {Token}", token[..16] + "...");
}

var toolRegistry = sp.GetRequiredService<AIToolRegistry>();

sp.GetRequiredService<LTAI.Agent.Evolution.PluginRegistry>().Discover();
await LTAI.Agent.Tools.ToolRegistryExtensions.RegisterAllToolCategoriesAsync(toolRegistry, logger);
// await LTAI.Core.Messaging.AIToolRegistry.SeedAllAsync(toolRegistry, sp);
// await sp.RegisterCodeActToolsAsync(toolRegistry);

await toolRegistry.RegisterAsync("git_diff", async args =>
{
    var repoPath = args.TryGetValue("repoPath", out var r) ? r?.ToString() : null;
    var files = args.TryGetValue("files", out var f) ? f?.ToString() : null;
    var staged = args.TryGetValue("staged", out var s) && s is true;
    return await LTAI.Agent.Tools.GitTools.GitDiff(repoPath, files, staged);
});
await toolRegistry.RegisterAsync("git_log", async args =>
{
    var repoPath = args.TryGetValue("repoPath", out var r) ? r?.ToString() : null;
    var maxCount = args.TryGetValue("maxCount", out var m) && int.TryParse(m?.ToString(), out var n) ? n : 20;
    var format = args.TryGetValue("format", out var f) ? f?.ToString() ?? "oneline" : "oneline";
    return await LTAI.Agent.Tools.GitTools.GitLog(repoPath, maxCount, format);
});
await toolRegistry.RegisterAsync("git_blame", async args =>
{
    var filePath = args.TryGetValue("filePath", out var fp) ? fp?.ToString() ?? "" : "";
    var repoPath = args.TryGetValue("repoPath", out var r) ? r?.ToString() : null;
    return await LTAI.Agent.Tools.GitTools.GitBlame(filePath, repoPath);
});

var system = sp.GetRequiredService<LivingTreeSystem>();
await system.InitializeAsync();

sp.GetRequiredService<LTAI.Agent.Evolution.HarnessSnapshot>().Capture();
sp.GetRequiredService<LTAI.Agent.Evolution.PluginRegistry>().Install("pr-review-toolkit", new()
{
    Name = "pr-review-toolkit", Version = "1.0", Type = "agent_bundle",
    Description = "6 specialized code review agents",
    Agents = new() { "comment-analyzer", "test-analyzer", "silent-failure-hunter", "type-design-analyzer", "code-reviewer", "code-simplifier" },
    Tools = new() { "code_analyze", "code_review", "git_diff", "code_stats" },
    Triggers = new() { "review", "check code", "test", "comment", "error", "refactor", "simplify" },
    Author = "LTAI", License = "MIT"
});

var evolEngine = sp.GetRequiredService<LTAI.Agent.Evolution.HarnessEvolutionEngine>();
evolEngine.RegisterComponent(new LTAI.Agent.Evolution.ToolsHarnessComponent(sp.GetRequiredService<AIToolRegistry>()));

var p2pNode = sp.GetRequiredService<IP2PNode>();
await p2pNode.StartAsync();
logger.LogInformation("P2P Node started: {PeerId} on port {Port}", p2pNode.PeerId, p2pNode.LocalPort);

var a2aP2pBridge = sp.GetRequiredService<A2aP2pBridge>();
await a2aP2pBridge.BroadcastAgentStatusAsync("LTAI", "online");

logger.LogInformation("LTAI running: mode={Mode} plugins={Plugins} tools={Tools}",
    system.Mode,
    sp.GetRequiredService<LTAI.Agent.Evolution.PluginRegistry>().Plugins.Count,
    sp.GetRequiredService<AIToolRegistry>().ListTools().Count());

app.Run();
