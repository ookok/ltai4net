using System.ComponentModel;
using LTAI.AI;
using LTAI.Core;
using SkiaSharp;

namespace LTAI.Agent.Tools;

[ToolDomain("multimedia")]
public sealed class ChartTools
{
    private readonly string _ws;
    public ChartTools(string ws) => _ws = ws;

    [Description("生成数据图表并保存为 PNG 图片。支持 bar（柱状图）、line（折线图）、pie（饼图）。\n"
        + "适用场景：数据可视化、报告图表、分析结果展示。\n"
        + "dataJson 格式：[{\"label\":\"A\",\"value\":10},{\"label\":\"B\",\"value\":20}]。\n"
        + "关键参数：type — 图表类型(bar/line/pie)；dataJson — JSON 数据数组；outputPath — 输出 PNG 路径。")]
    public string ChartCreate(string type, string dataJson, string outputPath, string? title = null, int width = 800, int height = 500)
    {
        var fp = PathUtils.SafeResolvePath(_ws, outputPath);
        if (fp == null) return "Error: path escape";
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);

        try
        {
            var data = System.Text.Json.JsonSerializer.Deserialize<List<ChartDataPoint>>(dataJson);
            if (data == null || data.Count == 0)
                return "Error: empty or invalid data";

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);

            switch (type.ToLowerInvariant())
            {
                case "bar": DrawBarChart(canvas, data, title, width, height); break;
                case "line": DrawLineChart(canvas, data, title, width, height); break;
                case "pie": DrawPieChart(canvas, data, title, width, height); break;
                default: return $"Unsupported chart type: {type}. Supported: bar, line, pie";
            }

            using var image = surface.Snapshot();
            using var dataPng = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(fp);
            dataPng.SaveTo(stream);

            return $"Chart saved: {fp} ({width}x{height}, {type})";
        }
        catch (Exception ex)
        {
            return $"Error creating chart: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void DrawBarChart(SKCanvas canvas, List<ChartDataPoint> data, string? title, int w, int h)
    {
        var margin = 60;
        var chartW = w - margin * 2;
        var chartH = h - margin * 2;
        var maxVal = data.Max(d => d.Value);
        var barW = chartW / (data.Count * 2 + 1);

        using var axisPaint = new SKPaint { Color = SKColors.Black, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var barFill = new SKPaint { Color = SKColors.SteelBlue, Style = SKPaintStyle.Fill };
        using var font12 = new SKFont(SKTypeface.Default, 12);
        using var font16 = new SKFont(SKTypeface.Default, 16);
        using var textPaint = new SKPaint { Color = SKColors.Black };

        canvas.DrawLine(margin, h - margin, w - margin, h - margin, axisPaint);
        canvas.DrawLine(margin, margin, margin, h - margin, axisPaint);

        for (int i = 0; i < data.Count; i++)
        {
            var barH = (float)(data[i].Value / maxVal * chartH);
            var x = margin + barW * (2 * i + 1);
            var y = h - margin - barH;
            canvas.DrawRect(x, y, barW, barH, barFill);
            canvas.DrawText(data[i].Label, x, h - margin + 16, SKTextAlign.Left, font12, textPaint);
            canvas.DrawText(data[i].Value.ToString("0.##"), x, y - 4, SKTextAlign.Left, font12, textPaint);
        }

        if (!string.IsNullOrEmpty(title))
            canvas.DrawText(title, w / 2f, 24, SKTextAlign.Center, font16, textPaint);
    }

    private static void DrawLineChart(SKCanvas canvas, List<ChartDataPoint> data, string? title, int w, int h)
    {
        var margin = 60;
        var chartW = w - margin * 2;
        var chartH = h - margin * 2;
        var maxVal = Math.Max(data.Max(d => d.Value), 1);
        var minVal = Math.Min(data.Min(d => d.Value), 0);
        var range = maxVal - minVal;

        using var axisPaint = new SKPaint { Color = SKColors.Black, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var linePaint = new SKPaint { Color = SKColors.Coral, StrokeWidth = 3, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var dotPaint = new SKPaint { Color = SKColors.Coral, Style = SKPaintStyle.Fill };
        using var font12 = new SKFont(SKTypeface.Default, 12);
        using var font16 = new SKFont(SKTypeface.Default, 16);
        using var textPaint = new SKPaint { Color = SKColors.Black };

        canvas.DrawLine(margin, h - margin, w - margin, h - margin, axisPaint);
        canvas.DrawLine(margin, margin, margin, h - margin, axisPaint);

        var points = new SKPoint[data.Count];
        for (int i = 0; i < data.Count; i++)
        {
            var x = margin + (float)i / (data.Count - 1) * chartW;
            var y = h - margin - (float)((data[i].Value - minVal) / range) * chartH;
            points[i] = new SKPoint(x, y);
        }

        if (points.Length >= 2)
            canvas.DrawPoints(SKPointMode.Polygon, points, linePaint);

        foreach (var pt in points)
            canvas.DrawCircle(pt, 4, dotPaint);

        for (int i = 0; i < data.Count; i++)
        {
            canvas.DrawText(data[i].Label, points[i].X - 10, h - margin + 16, SKTextAlign.Left, font12, textPaint);
            canvas.DrawText(data[i].Value.ToString("0.##"), points[i].X - 10, points[i].Y - 10, SKTextAlign.Left, font12, textPaint);
        }

        if (!string.IsNullOrEmpty(title))
            canvas.DrawText(title, w / 2f, 24, SKTextAlign.Center, font16, textPaint);
    }

    private static void DrawPieChart(SKCanvas canvas, List<ChartDataPoint> data, string? title, int w, int h)
    {
        var total = data.Sum(d => d.Value);
        if (total == 0) return;

        var colors = new SKColor[]
        {
            new(0x41, 0x6B, 0xE1), new(0xE1, 0x6B, 0x41), new(0x41, 0xE1, 0x6B),
            new(0xE1, 0x41, 0x6B), new(0x6B, 0x41, 0xE1), new(0xE1, 0xE1, 0x41),
            new(0x41, 0xE1, 0xE1), new(0xE1, 0x41, 0xE1)
        };

        var cx = w / 2f;
        var cy = h / 2f;
        var radius = Math.Min(w, h) / 2f - 60;
        var startAngle = 0f;

        using var font12 = new SKFont(SKTypeface.Default, 12);
        using var font16 = new SKFont(SKTypeface.Default, 16);
        using var textPaint = new SKPaint { Color = SKColors.Black };

        for (int i = 0; i < data.Count; i++)
        {
            var sweepAngle = (float)(data[i].Value / total * 360);
            using var paint = new SKPaint { Color = colors[i % colors.Length], Style = SKPaintStyle.Fill, IsAntialias = true };
            using var strokePaint = new SKPaint { Color = SKColors.White, StrokeWidth = 2, Style = SKPaintStyle.Stroke };

            using var path = new SKPath();
            path.MoveTo(cx, cy);
            path.ArcTo(new SKRect(cx - radius, cy - radius, cx + radius, cy + radius), startAngle, sweepAngle, false);
            path.Close();
            canvas.DrawPath(path, paint);
            canvas.DrawPath(path, strokePaint);

            var midAngle = (startAngle + sweepAngle / 2) * Math.PI / 180;
            var lx = cx + (float)(radius * 0.6 * Math.Cos(midAngle));
            var ly = cy + (float)(radius * 0.6 * Math.Sin(midAngle));
            canvas.DrawText($"{data[i].Label} ({data[i].Value / total * 100:0.0}%)", lx - 20, ly, SKTextAlign.Left, font12, textPaint);

            startAngle += sweepAngle;
        }

        if (!string.IsNullOrEmpty(title))
            canvas.DrawText(title, w / 2f, 20, SKTextAlign.Center, font16, textPaint);
    }

    private record ChartDataPoint(string Label, double Value);
}
