using System.Threading.RateLimiting;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Browser;
using LTAI.Browser.Interfaces;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Capability.Integration;
using LTAI.Document;
using LTAI.Economy;
using LTAI.Network;
using LTAI.Vector;
using LTAI.Vector.Interfaces;
using LTAI.Vector.Knowledge;
using LTAI.Web;
using LTAI.MAF;
using LTAI.DNA;
using LTAI.Capability;
using LTAI.Sandbox;
using LTAI.Metrics;
using LTAI.Multimodal;
using LTAI.TreeLLM;
using LTAI.Execution;
using LTAI.Memory;
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
var rateLimit = ltaiOptions.Web.RateLimitPerMinute;
if (rateLimit <= 0) rateLimit = 60;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("LTAI", config =>
    {
        config.PermitLimit = rateLimit;
        config.Window = TimeSpan.FromMinutes(1);
        config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        config.QueueLimit = 0;
    });
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        await context.HttpContext.Response.WriteAsync(
            "Rate limit exceeded. Try again later.", ct);
    };
});

builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, config) =>
{
    config.ReadFrom.Configuration(context.Configuration);
});

var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService("LTAI", serviceVersion: "5.5.0-net10");

builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(resourceBuilder);
    o.IncludeFormattedMessage = true;
});

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("LTAI.AI", "LTAI.TreeLLM", "LTAI.Execution")
        .AddConsoleExporter())
    .WithMetrics(m => m
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

builder.Services.AddHealthChecks()
    .AddCheck("liveness", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("LTAI is running"));

builder.Services.AddLTAICore();
builder.Services.AddLTAIVector();
builder.Services.AddLTAIAI();
builder.Services.AddLTAIVector();
builder.Services.AddLTAIDocument();
builder.Services.AddLTAIDNA();
builder.Services.AddLTAIMemory();
builder.Services.AddLTAITreeLLM();
builder.Services.AddLTAIExecution();
builder.Services.AddLTAICapability();
builder.Services.AddLTAIEconomy();
builder.Services.AddLTAISandbox();
builder.Services.AddLTAIMetrics();
builder.Services.AddLTAIMultimodal();
builder.Services.AddLTAITreeLLM();
builder.Services.AddLTAIExecution();
builder.Services.AddLTAIMemory();

var app = builder.Build();

ConfigureServices(app.Services);

await RegisterCapabilityTools(app.Services);

app.UseLTAI();

app.MapMAFEndpoints();
app.MapDNAEndpoints();
app.MapCapabilityEndpoints();
app.MapSandboxEndpoints();
app.UseLTAIMetrics();
app.MapMultimodalEndpoints();
app.MapExecutionEndpoints();

app.UseSerilogRequestLogging();

var system = app.Services.GetRequiredService<LivingTreeSystem>();
await system.InitializeAsync();

app.Logger.LogInformation("LTAI system initialized, mode: {Mode}", system.Mode);
app.Logger.LogInformation("Listening on http://{Host}:{Port}",
    ltaiOptions.Web.Host, ltaiOptions.Web.Port);

app.Run();

static void ConfigureServices(IServiceProvider sp)
{
    var vault = SecretVault.Instance;

    var gateway = sp.GetRequiredService<MessageGateway>();
    var smtpHost = vault.Get("smtp_host");
    if (!string.IsNullOrWhiteSpace(smtpHost))
    {
        var portStr = vault.Get("smtp_port", "465");
        _ = int.TryParse(portStr, out var port);
        if (port <= 0) port = 465;
        gateway.ConfigureSmtp(smtpHost, port, vault.Get("smtp_user"), vault.Get("smtp_pass"));
    }

    var weather = sp.GetRequiredService<WeatherService>();
    weather.OpenWeatherMapKey = vault.Get("openweathermap_api_key");
    weather.QWeatherKey = vault.Get("qweather_api_key");

    var imageSearch = sp.GetRequiredService<ImageSearchService>();
    imageSearch.UnsplashAccessKey = vault.Get("unsplash_access_key");
    imageSearch.PixabayApiKey = vault.Get("pixabay_api_key");

    var translate = sp.GetRequiredService<TranslateService>();
    translate.Config = new TranslateConfig
    {
        Provider = "baidu",
        AppId = vault.Get("baidu_translate_appid"),
        SecretKey = vault.Get("baidu_translate_key")
    };
}

static async Task RegisterCapabilityTools(IServiceProvider sp)
{
    var registry = sp.GetRequiredService<AIToolRegistry>();
    var browser = sp.GetRequiredService<IBrowserAgent>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

    await LTAI.Capability.Tools.LTAIToolRegistry.SeedAllAsync(registry, sp);

    await registry.RegisterAsync("browser_browse", async args =>
    {
        var url = args.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
        var task = args.TryGetValue("task", out var t) ? t?.ToString() ?? "extract content" : "extract content";
        var result = await browser.BrowseAsync(url, task);
        return new { result.Success, result.Url, result.Title, result.Items, result.Count, result.Method, result.ElapsedMs, result.Error };
    });

    await registry.RegisterAsync("browser_screenshot", async args =>
    {
        var result = await browser.ScreenshotAsync();
        return new { result.Success, result.Width, result.Height, result.Error,
            Base64Preview = result.Base64?.Length > 200 ? result.Base64[..200] : result.Base64 };
    });

    await registry.RegisterAsync("web_fetch", async args =>
    {
        var url = args.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var html = await http.GetStringAsync(url);
        var title = ExtractTitle(html);
        var text = StripHtml(html);
        text = text.Length > 5000 ? text[..5000] : text;
        return new { success = true, url, title, text, length = text.Length };
    });

    await registry.RegisterAsync("vector_search", async args =>
    {
        var query = args.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
            return new { error = "query required" };

        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var queryVec = await vectorStore.EmbedAsync(query);
        var results = await vectorStore.SearchSimilarAsync(queryVec, 5);
        return new { results = results.Select(r => new { r.Id, r.Score, r.Text }) };
    });

    await registry.RegisterAsync("doc_parse", async args =>
    {
        var filePath = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(filePath))
            return new { error = "path required" };

        var parser = sp.GetRequiredService<UniversalFileParser>();
        var result = await parser.ParseAsync(filePath);
        return new { result.Success, result.Format, result.ParserUsed, result.Text, result.Tables, result.Metadata, result.ElapsedMs, result.Error };
    });

    try
    {
        var codeAnalyzer = sp.GetRequiredService<LTAI.Capability.CodeEngine.MultiLangCodeAnalyzer>();
        await registry.RegisterAsync("code_analyze", async args =>
        {
            var code = args.TryGetValue("code", out var c) ? c?.ToString() ?? "" : "";
            var lang = args.TryGetValue("language", out var l) ? l?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(code))
                return new { error = "code required" };

            var language = Enum.TryParse<LTAI.Capability.CodeEngine.CodeLanguage>(lang, true, out var parsed) ? parsed : LTAI.Capability.CodeEngine.CodeLanguage.Unknown;
            var result = codeAnalyzer.Analyze(code, language);
            return new { result.LanguageName, result.TotalLines, result.CodeLines, result.Complexity, functions = result.Functions.Count, classes = result.Classes.Count, imports = result.Imports.Select(i => i.Module) };
        });
    }
    catch (Exception ex) { logger.LogWarning(ex, "Code analyze tool not registered"); }

    try
    {
        var searchEngine = sp.GetRequiredService<LTAI.Capability.Search.UnifiedSearchEngine>();
        await registry.RegisterAsync("search", async args =>
        {
            var query = args.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(query))
                return new { error = "query required" };

            var results = await searchEngine.SearchAsync(query, maxResults: 5);
            return new { count = results.Count, results = results.Select(r => new { r.Title, r.Url, r.Snippet, r.Source }) };
        });
    }
    catch (Exception ex) { logger.LogWarning(ex, "Search tool not registered"); }

    try
    {
        var reasoning = sp.GetRequiredService<LTAI.Capability.Reasoning.ReasoningOrchestrator>();
        await registry.RegisterAsync("reason", async args =>
        {
            var query = args.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(query))
                return new { error = "query required" };

            var report = await reasoning.ReasonAsync(query);
            return new
            {
                report.OverallConfidence,
                math = report.Math?.Result,
                logic = report.Logic?.Result,
                dialectical = report.Dialectical?.Result,
                attribution = report.Attribution?.Result
            };
        });
    }
    catch (Exception ex) { logger.LogWarning(ex, "Reasoning tool not registered"); }

    try
    {
        var sandbox = sp.GetRequiredService<LTAI.Sandbox.SandboxOrchestrator>();
        await registry.RegisterAsync("sandbox_exec", async args =>
        {
            var code = args.TryGetValue("code", out var c) ? c?.ToString() ?? "" : "";
            var lang = args.TryGetValue("language", out var l) ? l?.ToString() ?? "python" : "python";
            if (string.IsNullOrWhiteSpace(code))
                return new { error = "code required" };

            var language = Enum.TryParse<LTAI.Sandbox.SandboxLanguage>(lang, true, out var parsed) ? parsed : LTAI.Sandbox.SandboxLanguage.Python;
            var result = await sandbox.ExecuteAsync(code, language);
            return new { result.Success, result.Stdout, result.Stderr, result.ExecutionTimeMs, result.Error };
        });
    }
    catch (Exception ex) { logger.LogWarning(ex, "Sandbox tool not registered"); }

    try
    {
        var maps = sp.GetRequiredService<LTAI.Capability.GIS.UnifiedMapService>();
        await registry.RegisterAsync("gis_geocode", async args =>
        {
            var address = args.TryGetValue("address", out var a) ? a?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(address))
                return new { error = "address required" };
            var result = await maps.GeocodeAsync(address);
            return result != null ? new { result.Formatted, result.Lng, result.Lat, result.City } : new { error = "not found" };
        });
    }
    catch (Exception ex) { logger.LogWarning(ex, "GIS tool not registered"); }

    try
    {
        var review = sp.GetRequiredService<LTAI.Capability.Review.CodeReviewEngine>();
        await registry.RegisterAsync("code_review", async args =>
        {
            var scope = args.TryGetValue("scope", out var s) ? s?.ToString() ?? "staged" : "staged";
            var reviewScope = Enum.TryParse<LTAI.Capability.Review.ReviewScope>(scope, true, out var rs) ? rs : LTAI.Capability.Review.ReviewScope.Staged;
            var report = await review.ReviewAsync(null, reviewScope);
            return new
            {
                report.OverallScore, report.Summary,
                report.FilesChanged, report.TotalIssues,
                critical = report.CriticalIssues, warnings = report.Warnings, info = report.Infos,
                topIssues = report.Issues.Take(5).Select(i => new { i.File, i.Line, i.Title, i.Severity })
            };
        });
    }
    catch (Exception ex) { logger.LogWarning(ex, "Review tool not registered"); }

    logger.LogInformation("Registered {Count} capability tools", registry.ListTools().Count());

    try
    {
        await RegisterKernelMemoryTools(sp, registry);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Kernel Memory tools could not be registered");
    }
}

static async Task RegisterKernelMemoryTools(IServiceProvider sp, AIToolRegistry registry)
{
    var km = sp.GetRequiredService<KernelMemoryStore>();

    await registry.RegisterAsync("km_import", async args =>
    {
        var content = args.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
        var docId = args.TryGetValue("doc_id", out var id) ? id?.ToString() : null;
        if (string.IsNullOrWhiteSpace(content))
            return new { error = "content required" };

        var result = await km.ImportDocumentAsync(content, docId);
        return new { success = true, document_id = result };
    });

    await registry.RegisterAsync("km_search", async args =>
    {
        var query = args.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
        var limit = args.TryGetValue("limit", out var l) && int.TryParse(l?.ToString(), out var n) ? n : 5;
        if (string.IsNullOrWhiteSpace(query))
            return new { error = "query required" };

        var result = await km.SearchAsync(query, limit: limit);
        return new
        {
            results = result.Results.Select(r => new
            {
                r.SourceName,
                r.Link,
                r.SourceUrl,
                snippets = r.Partitions.Select(p => p.Text[..Math.Min(p.Text.Length, 300)])
            })
        };
    });

    await registry.RegisterAsync("km_ask", async args =>
    {
        var question = args.TryGetValue("question", out var q) ? q?.ToString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(question))
            return new { error = "question required" };

        var answer = await km.AskAsync(question);
        return new
        {
            answer.Result,
            answer.NoResult,
            sources = answer.RelevantSources.Select(s => new { s.SourceName, s.Link })
        };
    });
}

static string ExtractTitle(string html)
{
    var idx = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
    if (idx < 0) return "";
    idx += 7;
    var end = html.IndexOf("</title>", idx, StringComparison.OrdinalIgnoreCase);
    return end > idx ? html[idx..end].Trim() : "";
}

static string StripHtml(string html)
{
    var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
    return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
}
