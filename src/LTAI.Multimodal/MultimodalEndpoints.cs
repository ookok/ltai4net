using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Multimodal;

public static class MultimodalEndpoints
{
    public static void MapMultimodalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/ocr", async (HttpContext context, OCREngine ocr, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "Image file required" }, statusCode: 400);

            var lang = form["language"].FirstOrDefault() ?? "eng+chi_sim";
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var text = await ocr.ExtractTextFromBytesAsync(ms.ToArray(), lang, ct);
            return Results.Json(new { text });
        });

        endpoints.MapPost("/api/vision", async (HttpContext context, VisionAnalyzer vision, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "Image file required" }, statusCode: 400);

            var task = form["task"].FirstOrDefault();
            var tmp = Path.GetTempFileName() + Path.GetExtension(file.FileName);
            try
            {
                using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ct);
                var result = await vision.DescribeImageAsync(tmp, task, ct);
                return Results.Json(new { analysis = result });
            }
            finally { try { File.Delete(tmp); } catch { } }
        });

        endpoints.MapPost("/api/speech/tts", async (HttpContext context, SpeechEngine speech, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var req = JsonSerializer.Deserialize<SpeechRequest>(body);
            if (req == null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "Text required" }, statusCode: 400);

            try
            {
                var bytes = await speech.SynthesizeAsync(req.Text, req.Voice, ct);
                return Results.File(bytes, "audio/wav", "speech.wav");
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        endpoints.MapPost("/api/speech/stt", async (HttpContext context, SpeechEngine speech, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "Audio file required" }, statusCode: 400);

            var tmp = Path.GetTempFileName() + ".wav";
            try
            {
                using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ct);
                var text = await speech.RecognizeFromFileAsync(tmp, ct);
                return Results.Json(new { text });
            }
            finally { try { File.Delete(tmp); } catch { } }
        });

        endpoints.MapGet("/api/speech/voices", async (SpeechEngine speech, CancellationToken ct) =>
            Results.Json(await speech.GetAvailableVoicesAsync(ct)));

        endpoints.MapPost("/api/multimodal/process", async (HttpContext context, MultimodalOrchestrator mm, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "File required" }, statusCode: 400);

            var task = form["task"].FirstOrDefault();
            var tmp = Path.GetTempFileName() + Path.GetExtension(file.FileName);
            try
            {
                using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ct);
                var result = await mm.ProcessFileAsync(tmp, task, ct);
                return Results.Json(new { result, type = Path.GetExtension(file.FileName) });
            }
            finally { try { File.Delete(tmp); } catch { } }
        });
    }
}

public sealed record SpeechRequest
{
    public string Text { get; init; } = "";
    public string? Voice { get; init; }
}
