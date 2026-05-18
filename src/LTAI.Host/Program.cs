using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Browser;
using LTAI.Browser.Interfaces;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.Document;
using LTAI.Network;
using LTAI.Vector;
using LTAI.Vector.Interfaces;
using LTAI.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Services.Configure<LTAIOptions>(builder.Configuration.GetSection(LTAIOptions.SectionName));

builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

builder.Services.AddLTAICore();
builder.Services.AddLTAIVector();
builder.Services.AddLTAIAI();
builder.Services.AddLTAIBrowser();
builder.Services.AddLTAIDocument();
builder.Services.AddLTAINetwork();

var app = builder.Build();

await RegisterCapabilityTools(app.Services);

app.UseLTAI();

var system = app.Services.GetRequiredService<LivingTreeSystem>();
await system.InitializeAsync();

app.Logger.LogInformation("LTAI system initialized, mode: {Mode}", system.Mode);
app.Logger.LogInformation("Listening on http://{Host}:{Port}",
    builder.Configuration["LTAI:Web:Host"] ?? "0.0.0.0",
    builder.Configuration["LTAI:Web:Port"] ?? "8080");

app.Run();

static async Task RegisterCapabilityTools(IServiceProvider sp)
{
    var registry = sp.GetRequiredService<IToolRegistry>();
    var browser = sp.GetRequiredService<IBrowserAgent>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

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

    logger.LogInformation("Registered {Count} capability tools", registry.ListTools().Count());
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
