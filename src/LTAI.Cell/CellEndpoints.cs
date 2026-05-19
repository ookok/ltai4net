using System.Text.Json;
using LTAI.Cell.Lifecycle;
using LTAI.Cell.Training;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Cell;

public static class CellEndpoints
{
    public static void MapCellEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/cell/train", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<TrainStartRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.ModelName))
                return Results.Json(new { error = "model_name required" }, statusCode: 400);

            var task = await CellTrainer.Instance.StartTrainingAsync(
                request.ModelName,
                request.DatasetName ?? "default",
                request.HyperParams ?? new Dictionary<string, string>());

            return Results.Json(task);
        });

        endpoints.MapPost("/api/cell/train/{taskId}/epoch", async (
            string taskId,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<EpochUpdateRequest>(body);

            await CellTrainer.Instance.UpdateEpochAsync(taskId, request?.Loss ?? 0f);
            var task = CellTrainer.Instance.GetTask(taskId);
            return task == null
                ? Results.Json(new { error = "task not found" }, statusCode: 404)
                : Results.Json(task);
        });

        endpoints.MapPost("/api/cell/train/{taskId}/complete", async (
            string taskId,
            CancellationToken cancellationToken) =>
        {
            await CellTrainer.Instance.CompleteTrainingAsync(taskId);
            var task = CellTrainer.Instance.GetTask(taskId);
            return task == null
                ? Results.Json(new { error = "task not found" }, statusCode: 404)
                : Results.Json(task);
        });

        endpoints.MapGet("/api/cell/train", () =>
            Results.Json(CellTrainer.Instance.ListTasks()));

        endpoints.MapPost("/api/cell/mitosis", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<MitosisRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.ParentId))
                return Results.Json(new { error = "parent_id required" }, statusCode: 400);

            var result = await Mitosis.Instance.ForkModelAsync(
                request.ParentId,
                request.Traits ?? new Dictionary<string, float>());

            return Results.Json(result);
        });

        endpoints.MapGet("/api/cell/mitosis/{parentId}", (string parentId) =>
            Results.Json(Mitosis.Instance.GetLineage(parentId)));

        endpoints.MapPost("/api/cell/distill", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<DistillRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.TeacherModel))
                return Results.Json(new { error = "teacher_model required" }, statusCode: 400);

            var result = await Distillation.Instance.DistillAsync(
                request.TeacherModel,
                request.StudentModel ?? "student_default",
                request.KnowledgeKeys ?? Array.Empty<string>());

            return Results.Json(result);
        });

        endpoints.MapGet("/api/cell/dream", () =>
        {
            var cycle = DreamLearner.Instance.RunDreamCycleAsync(
                10, new[] { "pattern_a", "pattern_b", "pattern_c", "pattern_d" }).Result;
            return Results.Json(cycle);
        });

        endpoints.MapPost("/api/cell/regen", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<RegenRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Trigger))
                return Results.Json(new { error = "trigger required" }, statusCode: 400);

            var report = await Regen.Instance.HealAsync(
                request.Trigger,
                request.DamageScore ?? 0.5f);

            return Results.Json(report);
        });

        endpoints.MapGet("/api/cell/stats", () =>
        {
            var stats = new Dictionary<string, object>
            {
                ["trainer"] = CellTrainer.Instance.GetStats(),
                ["mitosis"] = Mitosis.Instance.GetStats(),
                ["distillation"] = Distillation.Instance.GetStats(),
                ["dream_learner"] = DreamLearner.Instance.GetStats(),
                ["regen"] = Regen.Instance.GetStats()
            };

            return Results.Json(stats);
        });
    }
}

public sealed record TrainStartRequest
{
    public string ModelName { get; init; } = string.Empty;
    public string? DatasetName { get; init; }
    public Dictionary<string, string>? HyperParams { get; init; }
}

public sealed record EpochUpdateRequest
{
    public float Loss { get; init; }
}

public sealed record MitosisRequest
{
    public string ParentId { get; init; } = string.Empty;
    public Dictionary<string, float>? Traits { get; init; }
}

public sealed record DistillRequest
{
    public string TeacherModel { get; init; } = string.Empty;
    public string? StudentModel { get; init; }
    public string[]? KnowledgeKeys { get; init; }
}

public sealed record RegenRequest
{
    public string Trigger { get; init; } = string.Empty;
    public float? DamageScore { get; init; }
}
