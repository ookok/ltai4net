using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

public static class ChatMessageRenderer
{
    private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void RenderResponse(StackPanel panel, string raw)
    {
        panel.Children.Clear();
        var cleaned = CleanResponse(raw);

        if (IsDiffContent(cleaned))
        {
            RenderDiffBlock(panel, cleaned);
            return;
        }

        var parts = SplitCodeBlocks(cleaned);

        foreach (var part in parts)
        {
            if (part.IsCode)
                RenderCodeBlock(panel, part.Content);
            else
                RenderTextBlock(panel, part.Content);
        }

        var imageMatches = Regex.Matches(raw, @"!\[.*?\]\(([^)]+)\)|@""([^""]+)""");
        foreach (Match m in imageMatches)
        {
            var imgPath = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(imgPath))
                _ = RenderInlineImage(panel, imgPath);
        }
    }

    public static void UpdateResponseText(StackPanel panel, ref TextBlock? currentText, string text)
    {
        if (currentText == null)
        {
            currentText = new TextBlock
            {
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Text = text
            };
            panel.Children.Add(currentText);
        }
        else if (currentText.Text != text)
        {
            currentText.Text = text;
        }
    }

    public static Button CopyButton(string content)
    {
        var btn = new Button
        {
            Content = "Copy",
            Width = 48,
            Height = 22,
            FontSize = 10,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim)
        };
        btn.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(btn)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(content);
            btn.Content = "Done";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            await Task.Delay(1500);
            btn.Content = "Copy";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        };
        return btn;
    }

    public static string CleanResponse(string raw) => raw;

    public static bool IsDiffContent(string text)
    {
        var lines = text.Split('\n');
        var diffMarkers = lines.Count(l => l.StartsWith("--- ") || l.StartsWith("+++ ") || l.StartsWith("@@ "));
        return diffMarkers >= 2;
    }

    public static void RenderDiffBlock(StackPanel panel, string diff)
    {
        var border = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.CodeBg),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.CodeBorder),
            BorderThickness = new(1),
            CornerRadius = new(4),
            Padding = new(8),
            Margin = new(0, 4),
        };
        var stack = new StackPanel();
        var lines = diff.Split('\n');

        foreach (var line in lines)
        {
            var color = LtaiTheme.TextPrimary;
            var prefix = "";

            if (line.StartsWith("--- ") || line.StartsWith("+++ "))
            {
                color = LtaiTheme.AccentInfo;
                prefix = "  ";
            }
            else if (line.StartsWith("@@ "))
            {
                color = LtaiTheme.AccentDNA;
                prefix = "  ";
            }
            else if (line.StartsWith("+") && !line.StartsWith("+++"))
            {
                color = Color.Parse("#4CAF50");
                prefix = "+";
            }
            else if (line.StartsWith("-") && !line.StartsWith("---"))
            {
                color = Color.Parse("#F44336");
                prefix = "-";
            }
            else
            {
                prefix = " ";
            }

            var tb = new TextBlock
            {
                Text = prefix + " " + line,
                FontFamily = new("Consolas"),
                FontSize = 12,
                Foreground = LtaiTheme.Sbb(color),
            };
            stack.Children.Add(tb);
        }
        border.Child = stack;
        panel.Children.Add(border);
    }

    public static string TruncateFilePreview(string content, string path, int maxLines = 10)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines) return content;
        var preview = string.Join("\n", lines.Take(maxLines));
        return $"{preview}\n\n... ({lines.Length - maxLines} more lines) — use read_file with range to see more";
    }

    public static List<(string Content, bool IsCode)> SplitCodeBlocks(string text)
    {
        var parts = new List<(string, bool)>();
        const string fence = "```";
        var i = 0;

        while (true)
        {
            var start = text.IndexOf(fence, i, StringComparison.Ordinal);
            if (start < 0)
            {
                var tail = text[i..].TrimEnd();
                if (tail.Length > 0) parts.Add((tail, false));
                break;
            }

            if (start > i)
            {
                var pre = text[i..start].TrimEnd();
                if (pre.Length > 0) parts.Add((pre, false));
            }

            var langEnd = text.IndexOf('\n', start + 3);
            var contentStart = langEnd >= 0 ? langEnd + 1 : start + 3;
            var end = text.IndexOf(fence, contentStart, StringComparison.Ordinal);
            if (end < 0) end = text.Length;

            var code = text[contentStart..end].TrimEnd();
            if (code.Length > 0) parts.Add((code, true));
            i = end + 3;
        }

        return parts;
    }

    public static async Task RenderInlineImage(StackPanel panel, string path)
    {
        try
        {
            var isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            string localPath;
            if (isUrl)
            {
                using var resp = await _sharedHttp.GetAsync(path);
                var ext = ".png";
                var urlPath = new Uri(path).AbsolutePath;
                var urlExt = Path.GetExtension(urlPath)?.ToLowerInvariant();
                if (urlExt is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
                    ext = urlExt;

                localPath = Path.Combine(Path.GetTempPath(), $"ltai_img_{Guid.NewGuid():N}{ext}");
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(localPath, bytes);
            }
            else
            {
                if (!File.Exists(path)) return;
                localPath = path;
            }

            var ext2 = Path.GetExtension(localPath).ToLowerInvariant();
            if (ext2 is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
            {
                var bitmap = await Task.Run(() => new Avalonia.Media.Imaging.Bitmap(localPath));
                var image = new Image
                {
                    Source = bitmap,
                    MaxWidth = 400,
                    MaxHeight = 300,
                    Stretch = Stretch.Uniform
                };
                var border = new Border
                {
                    BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                    BorderThickness = new(1),
                    CornerRadius = new(4),
                    Margin = new(0, 4),
                    Child = image
                };
                panel.Children.Add(border);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ChatMessageRenderer: Failed to render inline image: {ex.Message}");
        }
    }

    private static void RenderCodeBlock(StackPanel panel, string code)
    {
        var codeRow = new DockPanel { Margin = new(0, 2) };

        var codeBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.CodeBg),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.CodeBorder),
            BorderThickness = new(1),
            CornerRadius = new(4),
            Padding = new(8, 8, 8, 8)
        };
        var codeStack = new StackPanel();
        const string lang = "csharp";
        var keywords = MarkdownRenderer.GetKeywords(lang);
        var codeLines = code.Split('\n');
        var linePad = codeLines.Length.ToString().Length;
        const int maxLines = 50;

        for (int li = 0; li < codeLines.Length && li < maxLines; li++)
        {
            var lineRow = new DockPanel { Margin = new(0, 0, 0, 0) };
            lineRow.Children.Add(new TextBlock
            {
                Text = (li + 1).ToString().PadLeft(linePad),
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontFamily = new("Consolas"),
                FontSize = 11,
                Width = 30,
                TextAlignment = TextAlignment.Right,
                Margin = new(0, 0, 8, 0),
            });
            var tb = new TextBlock { FontFamily = new("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var tokens = MarkdownRenderer.TokenizeLine(codeLines[li], keywords);
            if (tokens.Count > 0)
                foreach (var (text, color) in tokens)
                    tb.Inlines!.Add(new Avalonia.Controls.Documents.Run { Text = text, Foreground = LtaiTheme.Sbb(color) });
            else
                tb.Text = " ";
            lineRow.Children.Add(tb);
            codeStack.Children.Add(lineRow);
        }

        if (codeLines.Length > maxLines)
        {
            codeStack.Children.Add(new TextBlock
            {
                Text = $"[... truncated: {codeLines.Length - maxLines} more lines]",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontFamily = new("Consolas"),
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Margin = new(linePad * 8 + 8, 2, 0, 0)
            });
        }

        codeBorder.Child = codeStack;
        codeRow.Children.Add(codeBorder);

        var copyBtn = CopyButton(code);
        DockPanel.SetDock(copyBtn, Dock.Right);
        copyBtn.HorizontalAlignment = HorizontalAlignment.Right;
        copyBtn.VerticalAlignment = VerticalAlignment.Top;
        copyBtn.Margin = new(4, 0, 0, 0);
        codeRow.Children.Add(copyBtn);

        panel.Children.Add(codeRow);
    }

    private static void RenderTextBlock(StackPanel panel, string text)
    {
        var stb = new SelectableTextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        MarkdownRenderer.Render(text, stb.Inlines!);
        panel.Children.Add(stb);
    }
}
