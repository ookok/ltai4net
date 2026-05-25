using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LTAI.Tools.CodeEngine;
using LTAI.Tools.CodeGraph;
using LTAI.Tools.Crawler;
using LTAI.Tools.DocEngine;
using LTAI.Tools.Documents;
using LTAI.Tools.Evolution;
using LTAI.Tools.GIS;
using LTAI.Tools.Integration;
using LTAI.Tools.Knowledge;
using LTAI.Tools.Lsp;
using LTAI.Core.System;
using LTAI.Tools.Pipeline;
using LTAI.Tools.Reasoning;
using LTAI.Tools.Review;
using LTAI.Tools.Search;
using LTAI.Tools.Skills;
using LTAI.Tools.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Tools;

public static class CapabilityEndpoints
{
    public static void MapCapabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/code/analyze", async (
            HttpContext context,
            MultiLangCodeAnalyzer analyzer,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<CodeAnalyzeRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return Results.Json(new { error = "Code content required" }, statusCode: 400);

            var lang = string.IsNullOrWhiteSpace(request.Language)
                ? DetectLanguage(request.Code)
                : Enum.Parse<CodeLanguage>(request.Language, true);

            var result = await analyzer.AnalyzeAsync(request.Code, lang).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/code/quality", async (
            HttpContext context,
            MultiLangCodeAnalyzer analyzer,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<CodeAnalyzeRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return Results.Json(new { error = "Code content required" }, statusCode: 400);

            var lang = string.IsNullOrWhiteSpace(request.Language)
                ? DetectLanguage(request.Code)
                : Enum.Parse<CodeLanguage>(request.Language, true);

            var result = analyzer.CheckQuality(request.Code, lang);
            return Results.Json(result);
        });

        endpoints.MapGet("/api/code/languages", () =>
            LanguageRegistry.Languages.Keys.Select(k => new { id = k.ToString(), name = LanguageRegistry.Languages[k].Name }));

        endpoints.MapPost("/api/search", async (
            HttpContext context, UnifiedSearchEngine engine, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<SearchRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "query required" }, statusCode: 400);
            var sources = request.Sources?.Select(s => Enum.Parse<SearchSource>(s, true)).ToArray();
            var results = await engine.SearchAsync(request.Query, sources, request.MaxResults ?? 5, ct).ConfigureAwait(false);
            return Results.Json(results);
        });

        endpoints.MapPost("/api/doc/parse", async (
            HttpContext context, DocumentProcessor processor, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<DocParseRequest>(body);
            if (req == null || string.IsNullOrWhiteSpace(req.FilePath))
                return Results.Json(new { error = "filePath required" }, statusCode: 400);
            var text = await processor.ExtractTextAsync(req.FilePath, ct).ConfigureAwait(false);
            var sections = await processor.ExtractSectionsAsync(req.FilePath, ct).ConfigureAwait(false);
            return Results.Json(new { text, sections });
        });

        endpoints.MapPost("/api/reason/math", async (HttpContext ctx, MathReasoner reasoner, CancellationToken ct) =>
        {
            using var r = new StreamReader(ctx.Request.Body);
            var req = JsonSerializer.Deserialize<ReasonRequest>(await r.ReadToEndAsync(ct).ConfigureAwait(false));
            if (req == null || string.IsNullOrWhiteSpace(req.Query))
                return Results.Json(new { error = "query required" }, statusCode: 400);
            var result = await reasoner.SolveAsync(req.Query, ct).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason/logic", async (HttpContext ctx, FormalLogicEngine engine, CancellationToken ct) =>
        {
            using var r = new StreamReader(ctx.Request.Body);
            var req = JsonSerializer.Deserialize<ReasonRequest>(await r.ReadToEndAsync(ct).ConfigureAwait(false));
            if (req == null || string.IsNullOrWhiteSpace(req.Query))
                return Results.Json(new { error = "query required" }, statusCode: 400);
            var mode = req.Mode != null ? Enum.Parse<ReasoningMode>(req.Mode, true) : ReasoningMode.Forward;
            var result = await engine.ReasonAsync(req.Query, mode, ct).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason/dialectical", async (HttpContext ctx, DialecticalReasoner reasoner, CancellationToken ct) =>
        {
            using var r = new StreamReader(ctx.Request.Body);
            var req = JsonSerializer.Deserialize<ReasonRequest>(await r.ReadToEndAsync(ct).ConfigureAwait(false));
            if (req == null || string.IsNullOrWhiteSpace(req.Query))
                return Results.Json(new { error = "query required" }, statusCode: 400);
            var result = await reasoner.AnalyzeAsync(req.Query, req.Thesis, ct).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason/attribution", async (HttpContext ctx, AttributionReasoner reasoner, CancellationToken ct) =>
        {
            using var r = new StreamReader(ctx.Request.Body);
            var req = JsonSerializer.Deserialize<ReasonRequest>(await r.ReadToEndAsync(ct).ConfigureAwait(false));
            if (req == null || string.IsNullOrWhiteSpace(req.Query))
                return Results.Json(new { error = "query required" }, statusCode: 400);
            var result = await reasoner.TraceAsync(req.Query, req.Evidence, ct).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason", async (HttpContext ctx, ReasoningOrchestrator orch, CancellationToken ct) =>
        {
            using var r = new StreamReader(ctx.Request.Body);
            var req = JsonSerializer.Deserialize<ReasonRequest>(await r.ReadToEndAsync(ct).ConfigureAwait(false));
            if (req == null || string.IsNullOrWhiteSpace(req.Query))
                return Results.Json(new { error = "query required" }, statusCode: 400);
            var types = req.Types?.Select(t => Enum.Parse<ReasoningType>(t, true)).ToArray();
            var report = await orch.ReasonAsync(req.Query, types, ct).ConfigureAwait(false);
            return Results.Json(report);
        });

        endpoints.MapGet("/api/gis/geocode", async (string address, UnifiedMapService gis, CancellationToken ct) =>
            Results.Json(await gis.GeocodeAsync(address, ct: ct)));

        endpoints.MapGet("/api/gis/reverse", async (double lng, double lat, UnifiedMapService gis, CancellationToken ct) =>
            Results.Json(await gis.ReverseGeocodeAsync(lng, lat, ct: ct)));

        endpoints.MapGet("/api/gis/poi", async (string keyword, string city, UnifiedMapService gis, CancellationToken ct) =>
            Results.Json(await gis.SearchPOIAsync(keyword, city, ct: ct)));

        endpoints.MapGet("/api/gis/route", async (string from, string to, string mode, UnifiedMapService gis, CancellationToken ct) =>
        {
            var fromParts = from.Split(',');
            var toParts = to.Split(',');
            var fromPoint = new GeoPoint { Lng = double.Parse(fromParts[0]), Lat = double.Parse(fromParts[1]) };
            var toPoint = new GeoPoint { Lng = double.Parse(toParts[0]), Lat = double.Parse(toParts[1]) };
            return Results.Json(await gis.GetRouteAsync(fromPoint, toPoint, mode, ct: ct).ConfigureAwait(false));
        });

        endpoints.MapGet("/api/gis/weather", async (string city, UnifiedMapService gis, CancellationToken ct) =>
            Results.Json(await gis.GetWeatherAsync(city, ct: ct)));

        endpoints.MapGet("/api/review", () => Results.Json(new { message = "POST /api/review with target param" }));

        endpoints.MapPost("/api/notify/telegram", async (
            HttpContext context, TelegramBot bot, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<NotifyRequest>(body);
            if (req == null || string.IsNullOrWhiteSpace(req.Message))
                return Results.Json(new { error = "message required" }, statusCode: 400);
            var ok = await bot.SendMessageAsync(req.ChatId, req.Message, ct).ConfigureAwait(false);
            return Results.Json(new { success = ok });
        });

        endpoints.MapPost("/api/notify/wework", async (
            HttpContext context, WechatWorkNotifier wework, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<NotifyRequest>(body);
            if (req == null || string.IsNullOrWhiteSpace(req.Message))
                return Results.Json(new { error = "message required" }, statusCode: 400);
            var ok = await wework.SendTextAsync(req.Message, ct: ct).ConfigureAwait(false);
            return Results.Json(new { success = ok });
        });

        endpoints.MapGet("/api/update/check", async (
            AutoUpdater updater, CancellationToken ct) =>
        {
            var result = await updater.CheckForUpdatesAsync(ct).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/gateway/send", async (
            HttpContext context, MessageGateway gateway, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<GatewayMessage>(body);
            if (req == null) return Results.Json(new { error = "invalid request" }, statusCode: 400);
            var result = await gateway.SendAsync(req).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapGet("/api/gateway/stats", (MessageGateway gateway) =>
            Results.Ok(gateway.GetStats()));

        endpoints.MapPost("/api/wework/verify", (WeWorkBot bot, string signature, string timestamp, string nonce, string echostr) =>
        {
            var result = bot.VerifyUrl(signature, timestamp, nonce, echostr);
            return string.IsNullOrEmpty(result) ? Results.BadRequest("verify failed") : Results.Text(result);
        });

        endpoints.MapPost("/api/wework/callback", async (
            HttpContext context, WeWorkBot bot, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var reply = await bot.HandleMessageAsync(body).ConfigureAwait(false);
            return reply is not null ? Results.Text(reply, "application/xml") : Results.Ok("success");
        });

        endpoints.MapPost("/api/pkg/install", async (
            HttpContext context, PkgManager pkg, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<InstallRequest>(body);
            if (req == null) return Results.Json(new { error = "invalid request" }, statusCode: 400);
            var result = await pkg.InstallNuGetAsync(req.PackageId, req.Version, req.Source).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapGet("/api/pkg/tools", async (PkgManager pkg) =>
            Results.Ok(await pkg.GetInstalledToolsAsync()));

        MapDeepEndpoints(endpoints);
    }

    private static void MapDeepEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/skills/discover", (SkillDiscoveryManager discovery) =>
            Results.Ok(discovery.DiscoverAll()));

        endpoints.MapPost("/api/skills/search", (SkillCatalog catalog, string query) =>
            Results.Ok(catalog.Search(query)));

        endpoints.MapPost("/api/skills/suggest", (SkillCatalog catalog, string task) =>
            Results.Ok(catalog.SuggestSkills(task)));

        endpoints.MapPost("/api/skills/import", async (HttpContext context) =>
        {
            var mdContent = "";
            if (context.Request.HasFormContentType)
            {
                var file = context.Request.Form.Files.FirstOrDefault();
                if (file is not null)
                {
                    using var reader = new StreamReader(file.OpenReadStream());
                    mdContent = await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }
            if (string.IsNullOrEmpty(mdContent))
            {
                using var reader = new StreamReader(context.Request.Body);
                mdContent = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var discovery = context.RequestServices.GetRequiredService<SkillDiscoveryManager>();
            var importer = new SkillMarkdownImporter(discovery);
            var result = importer.ImportFromMarkdown(mdContent);
            return Results.Ok(new { installed = result.Installed.Count, failed = result.Failed.Count, skills = result.Installed.Select(s => new { s.name, s.file }), errors = result.Failed });
        });

        endpoints.MapPost("/api/tools/list", (ToolMarket market) =>
            Results.Ok(market.Discover()));

        endpoints.MapPost("/api/tools/search", (ToolMarket market, string query) =>
            Results.Ok(market.Search(query)));

        endpoints.MapPost("/api/tools/synthesize/list", (ToolSynthesizer synth) =>
            Results.Ok(synth.ListTools()));

        endpoints.MapPost("/api/tools/snapshot/list", (ToolOrchestrator orch) =>
            Results.Ok(orch.SnapshotList()));

        endpoints.MapPost("/api/pipeline/format", (string text) =>
            Results.Ok(PipelineEngine.Format(text)));

        endpoints.MapPost("/api/codegraph/index", async (CodeGraphEnhanced graph) =>
        {
            await graph.IndexAsync().ConfigureAwait(false);
            return Results.Ok(graph.Stats());
        });

        endpoints.MapGet("/api/codegraph/search", (CodeGraphEnhanced graph, string query) =>
            Results.Ok(graph.Search(query)));

        endpoints.MapGet("/api/codegraph/hubs", (CodeGraphEnhanced graph, int topN = 10) =>
            Results.Ok(graph.FindHubs(topN)));

        endpoints.MapGet("/api/codegraph/blast", (CodeGraphEnhanced graph, string entity, int maxDepth = 2) =>
            Results.Ok(graph.BlastRadius(entity, maxDepth)));

        endpoints.MapPost("/api/crawler/fetch", async (LightCrawler crawler, string url) =>
            Results.Ok(await crawler.FetchAsync(url)));

        endpoints.MapPost("/api/evolution/discovery/stats", (SelfDiscovery discovery) =>
            Results.Ok(discovery.GetStats()));

        endpoints.MapPost("/api/evolution/discovery/proposals", (SelfDiscovery discovery) =>
            Results.Ok(discovery.GetProposals()));

        endpoints.MapPost("/api/evolution/document", async (SelfDocumenter doc) =>
        {
            var result = await doc.GenerateAsync().ConfigureAwait(false);
            return Results.Ok(new { title = result.Title, sections = result.Sections.Count });
        });

        endpoints.MapPost("/api/docengine/templates", (DocEngine.DocEngine engine) =>
            Results.Ok(engine.GetTemplateTypes()));

        endpoints.MapPost("/api/docengine/review", (DocForge forge, string content, string? previous) =>
            Results.Ok(forge.Review(content, previous)));

        endpoints.MapPost("/api/docengine/validate", (string content, string schemaType) =>
            Results.Ok(new { passed = DocForge.ValidateSchema(content, schemaType) }));

        endpoints.MapPost("/api/docengine/templates/search", (TemplateRegistry registry, string? query) =>
            Results.Ok(registry.Search(query)));

        endpoints.MapPost("/api/knowledge/forager/sites", (KnowledgeForager forager) =>
            Results.Ok(forager.GetDueSources()));

        endpoints.MapPost("/api/knowledge/forager/patrol", async (KnowledgeForager forager) =>
            Results.Ok(await forager.PatrolAsync()));

        endpoints.MapPost("/api/knowledge/forager/brief", async (KnowledgeForager forager) =>
            Results.Ok(await forager.GenerateDailyBriefAsync()));

        endpoints.Map("/api/lsp", async (HttpContext context, LspServer lspServer) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await HandleLspWebSocket(ws, lspServer).ConfigureAwait(false);
            }
            else
            {
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new { protocol = "lsp", transport = "websocket", version = "3.17" }));
            }
        });
    }

    private static async Task HandleLspWebSocket(WebSocket ws, LspServer lspServer)
    {
        var buffer = new byte[1024 * 64];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var response = await lspServer.HandleMessageAsync(json).ConfigureAwait(false);

            if (response != null)
            {
                var responseBytes = Encoding.UTF8.GetBytes(response);
                await ws.SendAsync(new ArraySegment<byte>(responseBytes),
                    WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static CodeLanguage DetectLanguage(string code)
    {
        var result = ClassificationRegistry.CodeLanguage.Classify(code);
        return result switch
        {
            "CSharp" => CodeLanguage.CSharp,
            "TypeScript" => CodeLanguage.TypeScript,
            "Python" => CodeLanguage.Python,
            "Go" => CodeLanguage.Go,
            "Rust" => CodeLanguage.Rust,
            "Java" => CodeLanguage.Java,
            "Sql" => CodeLanguage.Sql,
            "Html" => CodeLanguage.Html,
            _ => CodeLanguage.Unknown
        };
    }
}

public sealed record CodeAnalyzeRequest
{
    public string Code { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string? FilePath { get; init; }
}

public sealed record SearchRequest
{
    public string Query { get; init; } = string.Empty;
    public string[]? Sources { get; init; }
    public int? MaxResults { get; init; }
}

public sealed record DocParseRequest
{
    public string FilePath { get; init; } = string.Empty;
}

public sealed record NotifyRequest
{
    public string Message { get; init; } = string.Empty;
    public long ChatId { get; init; }
}

public sealed record ReasonRequest
{
    public string Query { get; init; } = string.Empty;
    public string? Thesis { get; init; }
    public string? Mode { get; init; }
    public string[]? Types { get; init; }
    public List<string>? Evidence { get; init; }
}

public sealed record InstallRequest
{
    public string PackageId { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Source { get; init; }
}
