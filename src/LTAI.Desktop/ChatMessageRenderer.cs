using System.Collections.Concurrent;
using System.Text;
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
    private static readonly Regex CitationRegex = new(@"\[@([^\]]+)\]\(line:(\d+)\)|@([^\s:,\)]+):(\d+)\b");
    private static readonly Regex FenceStartRx = new(@"^```(\w*)$", RegexOptions.Multiline);
    private static readonly Regex FenceEndRx = new(@"^```$", RegexOptions.Multiline);
    private static bool _fenceWarningShown;

    // LRU cache for rendered responses (keyed by hash of content)
    private const int MaxCacheEntries = 64;
    private static readonly ConcurrentDictionary<long, List<Avalonia.Controls.Control>> _renderCache = new();
    private static readonly ConcurrentQueue<long> _cacheOrder = new();

    private static long ContentHash(string text)
    {
        // FNV-1a 64-bit hash to minimize collisions vs string.GetHashCode()
        const ulong fnvPrime = 1099511628211UL;
        const ulong fnvOffset = 14695981039346656037UL;
        ulong hash = fnvOffset;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= fnvPrime;
        }
        return (long)hash;
    }

    private static void CacheAdd(long key, List<Avalonia.Controls.Control> children)
    {
        _renderCache[key] = children;
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var old))
            _renderCache.TryRemove(old, out _);
    }

    /// <summary>Check if markdown has an unclosed code fence (for streaming awareness).</summary>
    public static bool HasUnclosedFence(string text)
    {
        var has = LTAI.Core.Rendering.MarkdownUtils.HasUnclosedFence(text);
        if (has && !_fenceWarningShown)
        {
            _fenceWarningShown = true;

        }
        if (!has) _fenceWarningShown = false;
        return has;
    }

    /// <summary>当用户点击文件引用时触发，参数 (filePath, lineNumber)。</summary>
    public static Action<string, int>? OnNavigateToFile;

    public static void RenderResponse(StackPanel panel, string raw)
    {
        panel.Children.Clear();
        var cleaned = CleanResponse(raw);

        // Check cache
        var hash = ContentHash(raw);
        if (_renderCache.TryGetValue(hash, out var cached))
        {
            foreach (var c in cached)
                panel.Children.Add(c);
            return;
        }

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

        var citationMatches = CitationRegex.Matches(raw);
        if (citationMatches.Count > 0)
        {
            var rootDir = LTAI.Core.Configuration.SecretManager.Get("LTAI_PROJECT_ROOT") ?? Directory.GetCurrentDirectory();
            foreach (Match m in citationMatches)
            {
                var filePart = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
                var lineStr = m.Groups[2].Success ? m.Groups[2].Value : (m.Groups[4].Success ? m.Groups[4].Value : null);
                var line = lineStr != null && int.TryParse(lineStr, out var l) ? l : 0;

                string? resolvedPath = null;
                if (File.Exists(filePart))
                    resolvedPath = filePart;
                else if (File.Exists(Path.Combine(rootDir, filePart)))
                    resolvedPath = Path.Combine(rootDir, filePart);
                else
                {
                    var found = Directory.EnumerateFiles(rootDir, Path.GetFileName(filePart), SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) resolvedPath = found;
                }

                var chip = MakeCitationChip(filePart, line, resolvedPath);
                panel.Children.Add(chip);
            }
        }

        // Cache the rendered children (snapshot)
        var snapshot = panel.Children.ToList();
        CacheAdd(hash, snapshot);
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
        var cts = new CancellationTokenSource();
        btn.DetachedFromVisualTree += (_, _) => { cts.Cancel(); cts.Dispose(); };
        btn.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(btn)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(content);
            btn.Content = "Done";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            try { await Task.Delay(1500, cts.Token); }
            catch (OperationCanceledException) { return; }
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
            CornerRadius = LtaiTheme.Radius.Md,
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
                color = LtaiTheme.DiffGreen;
                prefix = "+";
            }
            else if (line.StartsWith("-") && !line.StartsWith("---"))
            {
                color = LtaiTheme.DiffRed;
                prefix = "-";
            }
            else
            {
                prefix = " ";
            }

            var tb = new TextBlock
            {
                Text = prefix + " " + line,
                FontFamily = LtaiTheme.CodeFont,
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

    public static Border MakeCitationChip(string displayName, int line, string? resolvedPath)
    {
        var text = line > 0 ? $"📄 {displayName}:{line}" : $"📄 {displayName}";
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 11,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
        };
        var chip = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            BorderThickness = new(1),
            CornerRadius = LtaiTheme.Radius.Sm,
            Padding = new(6, 2),
            Margin = new(0, 2, 4, 2),
            Child = tb,
            Cursor = resolvedPath != null ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : null,
        };
        if (resolvedPath != null)
        {
            chip.PointerEntered += (_, _) => chip.Background = LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay);
            chip.PointerExited += (_, _) => chip.Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
            chip.PointerPressed += (_, _) => OnNavigateToFile?.Invoke(resolvedPath, line);
        }
        return chip;
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
                    CornerRadius = LtaiTheme.Radius.Md,
                    Margin = new(0, 4),
                    Child = image
                };
                panel.Children.Add(border);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatMessageRenderer] RenderInlineImage: {ex.Message}"); }
    }

    private static void RenderCodeBlock(StackPanel panel, string code)
    {
        var codeRow = new DockPanel { Margin = new(0, 2) };

        var codeBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.CodeBg),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.CodeBorder),
            BorderThickness = new(1),
            CornerRadius = LtaiTheme.Radius.Md,
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
                FontFamily = LtaiTheme.CodeFont,
                FontSize = 11,
                Width = 30,
                TextAlignment = TextAlignment.Right,
                Margin = new(0, 0, 8, 0),
            });
            var tb = new TextBlock { FontFamily = LtaiTheme.CodeFont, FontSize = 12, TextWrapping = TextWrapping.Wrap };
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
                FontFamily = LtaiTheme.CodeFont,
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Margin = new(linePad * 8 + 8, 2, 0, 0)
            });
        }

        codeBorder.Child = codeStack;
        codeRow.Children.Add(codeBorder);

        var copyBtn = CopyButton(code);
        copyBtn.Opacity = 0;
        codeBorder.PointerEntered += (_, _) => copyBtn.Opacity = 1;
        codeBorder.PointerExited += (_, _) => copyBtn.Opacity = 0;
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
