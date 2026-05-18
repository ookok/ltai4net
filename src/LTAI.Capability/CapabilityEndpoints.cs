using System.Text.Json;
using LTAI.Capability.CodeEngine;
using LTAI.Capability.Documents;
using LTAI.Capability.Reasoning;
using LTAI.Capability.Search;
using LTAI.Capability.GIS;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Capability;

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
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<CodeAnalyzeRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return Results.Json(new { error = "Code content required" }, statusCode: 400);

            var lang = string.IsNullOrWhiteSpace(request.Language)
                ? CodeLanguage.Unknown
                : Enum.TryParse<CodeLanguage>(request.Language, true, out var l) ? l : CodeLanguage.Unknown;

            if (lang == CodeLanguage.Unknown && !string.IsNullOrWhiteSpace(request.FilePath))
                lang = LanguageRegistry.Detect(request.FilePath);

            if (lang == CodeLanguage.Unknown)
                lang = DetectLanguage(request.Code);

            var result = analyzer.Analyze(request.Code, lang);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/code/quality", async (
            HttpContext context,
            MultiLangCodeAnalyzer analyzer,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<CodeAnalyzeRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return Results.Json(new { error = "Code required" }, statusCode: 400);

            var lang = Enum.TryParse<CodeLanguage>(request.Language ?? "Unknown", true, out var l) ? l : CodeLanguage.Unknown;
            var report = analyzer.CheckQuality(request.Code, lang);
            return Results.Json(report);
        });

        endpoints.MapPost("/api/search", async (
            HttpContext context,
            UnifiedSearchEngine search,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<SearchRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "Query required" }, statusCode: 400);

            var sources = request.Sources?
                .Select(s => Enum.TryParse<SearchSource>(s, true, out var src) ? src : SearchSource.Web)
                .ToArray();

            var results = await search.SearchAsync(request.Query, sources, request.MaxResults ?? 10, cancellationToken);
            return Results.Json(new { query = request.Query, count = results.Count, results });
        });

        endpoints.MapPost("/api/doc/parse", async (
            HttpContext context,
            DocumentProcessor processor,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<DocParseRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.FilePath))
                return Results.Json(new { error = "File path required" }, statusCode: 400);

            if (!File.Exists(request.FilePath))
                return Results.Json(new { error = "File not found" }, statusCode: 404);

            try
            {
                var text = await processor.ExtractTextAsync(request.FilePath, cancellationToken);
                var sections = await processor.ExtractSectionsAsync(request.FilePath, cancellationToken);
                return Results.Json(new
                {
                    file = request.FilePath,
                    length = text.Length,
                    preview = text[..Math.Min(text.Length, 1000)],
                    sections = sections.Select(s => new { s.Heading, s.Level })
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapGet("/api/code/languages", () =>
        {
            var langs = LanguageRegistry.Languages.Values
                .Where(l => l.Language != CodeLanguage.Unknown)
                .Select(l => new
                {
                    name = l.Name,
                    language = l.Language.ToString(),
                    extensions = l.Extensions,
                    compiled = l.IsCompiled
                });
            return Results.Json(langs);
        });

        endpoints.MapPost("/api/reason/math", async (
            HttpContext context,
            MathReasoner math,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ReasonRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "Query required" }, statusCode: 400);

            var result = await math.SolveAsync(request.Query, cancellationToken);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason/logic", async (
            HttpContext context,
            FormalLogicEngine logic,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ReasonRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "Query required" }, statusCode: 400);

            var mode = Enum.TryParse<ReasoningMode>(request.Mode ?? "Forward", true, out var m) ? m : ReasoningMode.Forward;
            var result = await logic.ReasonAsync(request.Query, mode, cancellationToken);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason/dialectical", async (
            HttpContext context,
            DialecticalReasoner dialectical,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ReasonRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "Query required" }, statusCode: 400);

            var result = await dialectical.AnalyzeAsync(request.Query, request.Thesis, cancellationToken);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason/attribution", async (
            HttpContext context,
            AttributionReasoner attribution,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ReasonRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "Query required" }, statusCode: 400);

            var result = await attribution.TraceAsync(request.Query, request.Evidence, cancellationToken);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/reason", async (
            HttpContext context,
            ReasoningOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ReasonRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "Query required" }, statusCode: 400);

            var types = request.Types?
                .Select(t => Enum.TryParse<ReasoningType>(t, true, out var rt) ? rt : ReasoningType.Auto)
                .ToArray();

            var result = await orchestrator.ReasonAsync(request.Query, types, cancellationToken);
            return Results.Json(result);
        });

        endpoints.MapGet("/api/gis/geocode", async (
            string address, string? provider,
            UnifiedMapService maps, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(address))
                return Results.Json(new { error = "address required" }, statusCode: 400);
            var result = await maps.GeocodeAsync(address, provider ?? "auto", ct);
            return Results.Json(result);
        });

        endpoints.MapGet("/api/gis/reverse", async (
            double lng, double lat, string? provider,
            UnifiedMapService maps, CancellationToken ct) =>
        {
            var result = await maps.ReverseGeocodeAsync(lng, lat, provider ?? "auto", ct);
            return Results.Json(result);
        });

        endpoints.MapGet("/api/gis/poi", async (
            string keyword, string? city, int limit, string? provider,
            UnifiedMapService maps, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return Results.Json(new { error = "keyword required" }, statusCode: 400);
            var results = await maps.SearchPOIAsync(keyword, city, limit > 0 ? limit : 10, provider ?? "auto", ct);
            return Results.Json(new { count = results.Count, results });
        });

        endpoints.MapGet("/api/gis/route", async (
            double fromLng, double fromLat, double toLng, double toLat, string? mode,
            UnifiedMapService maps, CancellationToken ct) =>
        {
            var route = await maps.GetRouteAsync(
                new GeoPoint { Lng = fromLng, Lat = fromLat },
                new GeoPoint { Lng = toLng, Lat = toLat },
                mode ?? "driving", "auto", ct);
            return Results.Json(route);
        });

        endpoints.MapGet("/api/gis/weather", async (
            string city, UnifiedMapService maps, CancellationToken ct) =>
        {
            var result = await maps.GetWeatherAsync(city, ct: ct);
            return Results.Json(result);
        });
    }

    private static CodeLanguage DetectLanguage(string code)
    {
        if (code.Contains("using System") || code.Contains("namespace ")) return CodeLanguage.CSharp;
        if (code.Contains("import React") || code.Contains("export default")) return CodeLanguage.TypeScript;
        if (code.Contains("def ") && code.Contains("import ")) return CodeLanguage.Python;
        if (code.Contains("func ") && code.Contains("package ")) return CodeLanguage.Go;
        if (code.Contains("fn ") && code.Contains("let mut")) return CodeLanguage.Rust;
        if (code.Contains("public class ") && code.Contains("void ")) return CodeLanguage.Java;
        if (code.Contains("SELECT ") || code.Contains("CREATE TABLE")) return CodeLanguage.Sql;
        if (code.Contains("<!DOCTYPE html") || code.Contains("<div")) return CodeLanguage.Html;
        return CodeLanguage.Unknown;
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

public sealed record ReasonRequest
{
    public string Query { get; init; } = string.Empty;
    public string? Thesis { get; init; }
    public string? Mode { get; init; }
    public string[]? Types { get; init; }
    public List<string>? Evidence { get; init; }
}
