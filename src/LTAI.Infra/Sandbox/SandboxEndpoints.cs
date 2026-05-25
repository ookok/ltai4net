using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Infra.Sandbox;

public static class SandboxEndpoints
{
    public static void MapSandboxEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/sandbox/execute", async (
            HttpContext context,
            SandboxOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<SandboxExecuteRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return Results.Json(new { error = "Code required" }, statusCode: 400);

            var lang = Enum.TryParse<SandboxLanguage>(request.Language ?? "Python", true, out var l) ? l : SandboxLanguage.Python;
            var result = await orchestrator.ExecuteAsync(
                request.Code, lang,
                request.TimeoutSeconds ?? 30,
                request.MemoryMb ?? 256,
                request.AllowNetwork ?? false,
                cancellationToken).ConfigureAwait(false);

            return Results.Json(new
            {
                result.Success, result.Stdout, result.Stderr,
                result.ExitCode, result.ExecutionTimeMs, result.PeakMemoryKb,
                result.Error, result.TimedOut
            });
        });

        endpoints.MapPost("/api/sandbox/template", async (
            HttpContext context,
            SandboxOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<SandboxExecuteRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return Results.Json(new { error = "Code required" }, statusCode: 400);

            var lang = Enum.TryParse<SandboxLanguage>(request.Language ?? "Python", true, out var l) ? l : SandboxLanguage.Python;
            var template = orchestrator.GenerateTemplate(lang, request.TaskDescription ?? "solve");
            var results = await orchestrator.ExecuteTemplateAsync(template, request.Code, lang, cancellationToken).ConfigureAwait(false);

            return Results.Json(new { template = template[..Math.Min(template.Length, 500)], results });
        });

        endpoints.MapGet("/api/sandbox/status", async (IEnumerable<ISandbox> sandboxes) =>
        {
            var statuses = new List<object>();
            foreach (var sb in sandboxes)
            {
                try
                {
                    var available = await sb.IsAvailableAsync().ConfigureAwait(false);
                    statuses.Add(new { name = sb.Name, available, capability = sb.Capability.ToString() });
                }
                catch
                {
                    statuses.Add(new { name = sb.Name, available = false, capability = sb.Capability.ToString() });
                }
            }
            return Results.Json(statuses);
        });
    }
}

public sealed record SandboxExecuteRequest
{
    public string Code { get; init; } = string.Empty;
    public string? Language { get; init; }
    public int? TimeoutSeconds { get; init; }
    public int? MemoryMb { get; init; }
    public bool? AllowNetwork { get; init; }
    public string? TaskDescription { get; init; }
}
