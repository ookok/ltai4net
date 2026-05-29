using System;
using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using FFMpegCore;
using MetadataExtractor;
using UglyToad.PdfPig;

namespace LTAI.Agent.Agents;

public sealed class MultimediaAgent : BaseAgent
{
    public MultimediaAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<MultimediaAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct)
    {
        var q = context.UserQuery;

        if (q.Contains("pdf", OrdinalIgnoreCase) && (q.Contains("read", OrdinalIgnoreCase) || q.Contains("extract", OrdinalIgnoreCase)))
            return await ReadPdfAsync(q, ct);
        if (q.Contains("image", OrdinalIgnoreCase) || q.Contains("photo", OrdinalIgnoreCase) || q.Contains("resize", OrdinalIgnoreCase))
            return await ProcessImageAsync(q);
        if (q.Contains("video", OrdinalIgnoreCase) || q.Contains("audio", OrdinalIgnoreCase) || q.Contains("ffmpeg", OrdinalIgnoreCase))
            return await ProcessMediaAsync(q);
        if (q.Contains("metadata", OrdinalIgnoreCase) || q.Contains("exif", OrdinalIgnoreCase))
            return await ReadMetadataAsync(q);

        return await CallBrainAsync(context.FullHistory, ct: ct);
    }

    private async Task<AgentResponse> ReadPdfAsync(string query, CancellationToken ct)
    {
        var path = ExtractPath(query);
        if (path == null || !File.Exists(path)) return Fail("File not found.");

        using var pdf = PdfDocument.Open(path);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PDF: {Path.GetFileName(path)} ({pdf.NumberOfPages} pages)");
        for (int i = 0; i < Math.Min(pdf.NumberOfPages, 5); i++)
        {
            var page = pdf.GetPage(i + 1);
            sb.AppendLine($"\n--- Page {i + 1} ---\n{page.Text[..Math.Min(page.Text.Length, 500)]}");
        }
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    private AgentResponse ProcessImageAsync(string query)
    {
        var path = ExtractPath(query);
        if (path == null || !File.Exists(path)) return Fail("File not found.");

        using var image = Image.Load(path);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Image: {Path.GetFileName(path)}");
        sb.AppendLine($"Size: {image.Width}x{image.Height}");
        sb.AppendLine($"Format: {image.Metadata.DecodedImageFormat?.Name}");

        if (query.Contains("resize", OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(query, @"(\d+)x(\d+)");
            if (m.Success)
            {
                var w = int.Parse(m.Groups[1].Value);
                var h = int.Parse(m.Groups[2].Value);
                image.Mutate(x => x.Resize(w, h));
                var outPath = Path.ChangeExtension(path, ".resized" + Path.GetExtension(path));
                image.Save(outPath);
                sb.AppendLine($"Resized to {w}x{h} → {outPath}");
            }
        }
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    private async Task<AgentResponse> ProcessMediaAsync(string query)
    {
        var path = ExtractPath(query);
        if (path == null || !File.Exists(path)) return Fail("File not found.");

        var info = await FFProbe.AnalyseAsync(path);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Media: {Path.GetFileName(path)}");
        sb.AppendLine($"Duration: {info.Duration}");
        sb.AppendLine($"Format: {info.Format.FormatName}");
        foreach (var s in info.PrimaryMediaStream)
            sb.AppendLine($"Stream: {s.Codec} {s.Width}x{s.Height} {s.FrameRate}FPS");
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    private async Task<AgentResponse> ReadMetadataAsync(string query)
    {
        var path = ExtractPath(query);
        if (path == null || !File.Exists(path)) return Fail("File not found.");

        var directories = ImageMetadataReader.ReadMetadata(path);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Metadata: {Path.GetFileName(path)}");
        foreach (var dir in directories)
        {
            sb.AppendLine($"\n[{dir.Name}]");
            foreach (var tag in dir.Tags.Take(10))
                sb.AppendLine($"  {tag.Name} = {tag.Description}");
        }
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    private static string? ExtractPath(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"[\w\\/:]+\.\w{2,4}");
        return m.Success ? m.Value : null;
    }
}


