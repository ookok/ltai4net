using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Tools;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.DNA;
using LTAI.Agent;
using LTAI.Memory;
using LTAI.Planning.Metrics;
using LTAI.Knowledge.Vector;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var ltaiSection = builder.Configuration.GetSection("LTAI");
builder.Services.Configure<LTAIOptions>(ltaiSection);

var ltaiOptions = ltaiSection.Get<LTAIOptions>() ?? new LTAIOptions();
builder.Services.AddSingleton(Options.Create(ltaiOptions));

builder.Services.AddLTAICore();
builder.Services.AddLTAIVectorAuto(apiModel: ltaiOptions.AI.L0.Model);
builder.Services.AddLTAIAI();
builder.Services.AddLTAIDNA();
builder.Services.AddLTAIMemory();
builder.Services.AddLTAICapability();
builder.Services.AddLTAIMetrics();
builder.Services.AddLTAIMAF();

builder.Services.AddSingleton<LivingTreeSystem>();
builder.Services.AddSingleton<DNAOrchestrator>();
builder.Services.AddSingleton<LTAIMetricsCollector>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapDevUIEndpoints();

app.MapRazorComponents<LTAI.WebApp.Components.App>();

var lts = app.Services.GetRequiredService<LivingTreeSystem>();
await lts.InitializeAsync();

app.Run();
