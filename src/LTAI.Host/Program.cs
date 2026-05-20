using System.Threading.RateLimiting;
using LTAI.Core.Configuration;
using LTAI.Core.Messaging;
using LTAI.MAF;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Core;
using LTAI.Web;
using LTAI.DNA;
using LTAI.Capability;
using LTAI.Sandbox;
using LTAI.Metrics;
using LTAI.Multimodal;
using LTAI.Execution;
using LTAI.Memory;
using LTAI.Document;
using LTAI.Vector;
using LTAI.Browser;
using LTAI.Network;
using LTAI.Economy;
using LTAI.TreeLLM;
using LTAI.Capability.Tools;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

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

builder.Services.AddLTAICore(); builder.Services.AddLTAIVector(); builder.Services.AddLTAIAI();
builder.Services.AddLTAIDocument(); builder.Services.AddLTAIDNA(); builder.Services.AddLTAIMemory();
builder.Services.AddLTAITreeLLM(); builder.Services.AddLTAIExecution(); builder.Services.AddLTAICapability();
builder.Services.AddLTAIEconomy(); builder.Services.AddLTAISandbox(); builder.Services.AddLTAIMetrics();
builder.Services.AddLTAIMultimodal(); builder.Services.AddLTAIBrowser(); builder.Services.AddLTAIMAF();
builder.Services.AddLTAINetwork();

var app = builder.Build();

ActivityFeedBridge.BridgeToOpenTelemetry();
app.UseA2ABearerAuth();
app.UseLTAI();

app.MapMAFEndpoints(); app.MapDNAEndpoints(); app.MapCapabilityEndpoints();
app.MapSandboxEndpoints(); app.MapMultimodalEndpoints(); app.MapExecutionEndpoints();
app.UseLTAIMetrics(); app.MapMCPEndpoints(); app.MapAHEEndpoints();
app.MapSpecializedA2AEndpoints(); app.MapA2AHttpJson("LTAI", "/a2a/livingtree");

app.UseSerilogRequestLogging();

var sp = app.Services;
var config = AppConfiguration.Load();
var logger = sp.GetRequiredService<ILogger<Program>>();

var toolRegistry = sp.GetRequiredService<AIToolRegistry>();

sp.GetRequiredService<LTAI.MAF.Evolution.PluginRegistry>().Discover();
await LTAI.MAF.Tools.ToolRegistryExtensions.RegisterAllToolCategoriesAsync(toolRegistry, logger);
await LTAI.Capability.Tools.LTAIToolRegistry.SeedAllAsync(toolRegistry, sp);

await toolRegistry.RegisterAsync("git_diff", async args =>
{
    var repoPath = args.TryGetValue("repoPath", out var r) ? r?.ToString() : null;
    var files = args.TryGetValue("files", out var f) ? f?.ToString() : null;
    var staged = args.TryGetValue("staged", out var s) && s is true;
    return await LTAI.MAF.Tools.GitTools.GitDiff(repoPath, files, staged);
});
await toolRegistry.RegisterAsync("git_log", async args =>
{
    var repoPath = args.TryGetValue("repoPath", out var r) ? r?.ToString() : null;
    var maxCount = args.TryGetValue("maxCount", out var m) && int.TryParse(m?.ToString(), out var n) ? n : 20;
    var format = args.TryGetValue("format", out var f) ? f?.ToString() ?? "oneline" : "oneline";
    return await LTAI.MAF.Tools.GitTools.GitLog(repoPath, maxCount, format);
});
await toolRegistry.RegisterAsync("git_blame", async args =>
{
    var filePath = args.TryGetValue("filePath", out var fp) ? fp?.ToString() ?? "" : "";
    var repoPath = args.TryGetValue("repoPath", out var r) ? r?.ToString() : null;
    return await LTAI.MAF.Tools.GitTools.GitBlame(filePath, repoPath);
});

var system = sp.GetRequiredService<LivingTreeSystem>();
await system.InitializeAsync();

sp.GetRequiredService<LTAI.MAF.Evolution.HarnessSnapshot>().Capture();
sp.GetRequiredService<LTAI.MAF.Evolution.PluginRegistry>().Install("pr-review-toolkit", new()
{
    Name = "pr-review-toolkit", Version = "1.0", Type = "agent_bundle",
    Description = "6 specialized code review agents",
    Agents = new() { "comment-analyzer", "test-analyzer", "silent-failure-hunter", "type-design-analyzer", "code-reviewer", "code-simplifier" },
    Tools = new() { "code_analyze", "code_review", "git_diff", "code_stats" },
    Triggers = new() { "review", "check code", "test", "comment", "error", "refactor", "simplify" },
    Author = "LTAI", License = "MIT"
});

var evolEngine = sp.GetRequiredService<LTAI.MAF.Evolution.HarnessEvolutionEngine>();
evolEngine.RegisterComponent(new LTAI.MAF.Evolution.ToolsHarnessComponent(sp.GetRequiredService<AIToolRegistry>()));

logger.LogInformation("LTAI running: mode={Mode} plugins={Plugins} tools={Tools}",
    system.Mode,
    sp.GetRequiredService<LTAI.MAF.Evolution.PluginRegistry>().Plugins.Count,
    sp.GetRequiredService<AIToolRegistry>().ListTools().Count());

app.Run();
