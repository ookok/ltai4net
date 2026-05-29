using System.Text.Json;
using LTAI.Core.Acceleration;
using LTAI.Core.Governors;
using LTAI.Core.Prefs;
using LTAI.Core.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Core;

public static class CoreEndpoints
{
    public static void MapCoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/core/gpu", () =>
            Results.Json(HardwareAcceleration.Instance.Stats()));

        endpoints.MapPost("/api/core/compress", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<CompressRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Content))
                return Results.Json(new { error = "content required" }, statusCode: 400);
            var (compressed, filter, savedPct) = TokenCompressor.Compress(request.Content);
            return Results.Json(new { compressed, filter, saved_pct = savedPct });
        });

        endpoints.MapGet("/api/core/shell/tools", () =>
            Results.Json(new { summary = ShellEnv.Instance.ProbeSummary() }));

        endpoints.MapPost("/api/core/shell/exec", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ShellExecRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Command))
                return Results.Json(new { error = "command required" }, statusCode: 400);
            var result = await ShellEnv.Instance.Execute(request.Command, request.Workdir ?? ".");
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/shield/input", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ShieldInputRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Text))
                return Results.Json(new { error = "text required" }, statusCode: 400);
            var result = PromptShield.Instance.SanitizeInput(request.Text);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/shield/output", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ShieldOutputRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Text))
                return Results.Json(new { error = "text required" }, statusCode: 400);
            var result = PromptShield.Instance.CheckOutput(request.Text, request.Context ?? "public");
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/tree/read", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<TreeReadRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { error = "path required" }, statusCode: 400);
            var result = await ResourceTree.Instance.Read(request.Path).ConfigureAwait(false);
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/atomic/apply", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<AtomicApplyRequest>(body);
            if (request == null || request.Edits == null || request.Edits.Count == 0)
                return Results.Json(new { error = "edits required" }, statusCode: 400);
            var result = await AtomicModification.Instance.Apply(request.Edits, request.Reason ?? "");
            return Results.Json(result);
        });

        endpoints.MapPost("/api/core/scanner/discover", (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<ScannerDiscoverRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Description))
                return Results.Json(new { error = "description required" }, statusCode: 400);
            var results = UniversalScanner.Instance.DiscoverFromDescription(request.Description);
            return Results.Json(results);
        });

        endpoints.MapGet("/api/core/prefs", () =>
            Results.Json(DpoPrefs.Instance.Stats()));

        endpoints.MapPost("/api/core/prefs/route", async (HttpContext context, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<PrefsRouteRequest>(body);
            if (request == null || string.IsNullOrWhiteSpace(request.Entity))
                return Results.Json(new { error = "entity required" }, statusCode: 400);
            var candidates = request.Entity.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var choice = DpoPrefs.Instance.Router.RouteSkill(request.Context ?? "", candidates);
            return Results.Json(new { entity = request.Entity, context = request.Context, choice });
        });
    }
}

public sealed record CompressRequest
{
    public string Content { get; init; } = string.Empty;
}

public sealed record ShellExecRequest
{
    public string Command { get; init; } = string.Empty;
    public string? Workdir { get; init; }
}

public sealed record ShieldInputRequest
{
    public string Text { get; init; } = string.Empty;
}

public sealed record ShieldOutputRequest
{
    public string Text { get; init; } = string.Empty;
    public string? Context { get; init; }
}

public sealed record TreeReadRequest
{
    public string Path { get; init; } = string.Empty;
}

public sealed record AtomicApplyRequest
{
    public Dictionary<string, string>? Edits { get; init; }
    public string? Reason { get; init; }
}

public sealed record ScannerDiscoverRequest
{
    public string Description { get; init; } = string.Empty;
}

public sealed record PrefsRouteRequest
{
    public string Entity { get; init; } = string.Empty;
    public string? Context { get; init; }
}
