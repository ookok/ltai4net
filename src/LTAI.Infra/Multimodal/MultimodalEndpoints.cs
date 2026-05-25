using System.Text.Json;
using LTAI.Core.Multimodal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Infra.Multimodal;

public static class MultimodalEndpoints
{
    public static void MapMultimodalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/ocr", async (HttpContext context, OCREngine ocr, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "Image file required" }, statusCode: 400);

            if (file.Length > 50 * 1024 * 1024)
                return Results.Json(new { error = "Image too large (max 50MB)" }, statusCode: 400);

            var lang = form["language"].FirstOrDefault() ?? "eng+chi_sim";
            using var ms = new MemoryStream((int)file.Length);
            await file.CopyToAsync(ms, ct).ConfigureAwait(false);
            var text = await ocr.ExtractTextFromBytesAsync(ms.ToArray(), lang, ct).ConfigureAwait(false);
            return Results.Json(new { text });
        });

        endpoints.MapPost("/api/vision", async (HttpContext context, VisionAnalyzer vision, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "Image file required" }, statusCode: 400);

            var task = form["task"].FirstOrDefault();
            var tmp = Path.GetTempFileName() + Path.GetExtension(file.FileName);
            try
            {
                using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ct).ConfigureAwait(false);
                var result = await vision.DescribeImageAsync(tmp, task, ct).ConfigureAwait(false);
                return Results.Json(new { analysis = result });
            }
            finally { try { File.Delete(tmp); } catch (Exception) { } }
        });

        endpoints.MapPost("/api/speech/tts", async (HttpContext context, ITtsEngine tts, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<SpeechRequest>(body);
            if (req == null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "Text required" }, statusCode: 400);

            try
            {
                var options = new TtsSynthesisOptions
                {
                    Voice = req.Voice,
                    Speed = req.Speed > 0 ? req.Speed : 1.05f,
                    Format = req.Format ?? "wav",
                    Lang = req.Lang
                };
                var result = await tts.SynthesizeAsync(req.Text, options, ct).ConfigureAwait(false);
                if (!result.Ok)
                    return Results.Json(new { error = result.Error }, statusCode: 500);

                return Results.File(result.AudioBytes, req.Format == "mp3" ? "audio/mpeg" : "audio/wav",
                    $"speech.{result.Format}");
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        endpoints.MapPost("/api/speech/stt", async (HttpContext context, SpeechEngine speech, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "Audio file required" }, statusCode: 400);

            var tmp = Path.GetTempFileName() + ".wav";
            try
            {
                using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ct).ConfigureAwait(false);
                var text = await speech.RecognizeFromFileAsync(tmp, ct).ConfigureAwait(false);
                return Results.Json(new { text });
            }
            finally { try { File.Delete(tmp); } catch (Exception) { } }
        });

        endpoints.MapGet("/api/speech/voices", async (ITtsEngine tts, CancellationToken ct) =>
        {
            var voices = await tts.GetVoicesAsync(ct).ConfigureAwait(false);
            return Results.Json(new { engine = tts.EngineName, voices });
        });

        endpoints.MapPost("/api/multimodal/process", async (HttpContext context, MultimodalOrchestrator mm, CancellationToken ct) =>
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.FirstOrDefault();
            if (file == null) return Results.Json(new { error = "File required" }, statusCode: 400);

            var task = form["task"].FirstOrDefault();
            var tmp = Path.GetTempFileName() + Path.GetExtension(file.FileName);
            try
            {
                using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ct).ConfigureAwait(false);
                var result = await mm.ProcessFileAsync(tmp, task, ct).ConfigureAwait(false);
                return Results.Json(new { result, type = Path.GetExtension(file.FileName) });
            }
            finally { try { File.Delete(tmp); } catch (Exception) { } }
        });
    }
}

public sealed record SpeechRequest
{
    public string Text { get; init; } = "";
    public string? Voice { get; init; }
    public float Speed { get; init; } = 1.05f;
    public string? Format { get; init; }
    public string? Lang { get; init; }
}
