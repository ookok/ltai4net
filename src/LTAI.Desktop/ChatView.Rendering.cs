using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LTAI.Desktop;

public sealed partial class ChatView : UserControl
{
    private void RenderResponse(StackPanel panel, string raw)
    {
        panel.Children.Clear();
        ChatMessageRenderer.RenderResponse(panel, raw);
    }

    private void UpdateResponseText(StackPanel panel, string text)
    {
        if (_currentResponseText == null)
        {
            _currentResponseText = new TextBlock
            {
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnBubble),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Text = text
            };
            panel.Children.Add(_currentResponseText);
        }
        else if (_currentResponseText.Text != text)
        {
            _currentResponseText.Text = text;
        }
    }

    private static Button CopyButton(string content)
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
            var topLevel = TopLevel.GetTopLevel(btn);
            if (topLevel?.Clipboard != null)
                await topLevel.Clipboard.SetTextAsync(content);
            btn.Content = "Done";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            try { await Task.Delay(1500, cts.Token); }
            catch (OperationCanceledException) { return; }
            btn.Content = "Copy";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        };
        return btn;
    }

    /// <summary>
    /// Clean raw response for rendering. Previously stripped surrogate pairs
    /// (which killed emoji like 📋 from ReAct tool calls). Now passes through
    /// all characters — code-block fences and markdown are handled elsewhere.
    /// </summary>
    private static string CleanResponse(string raw) => raw;

    // ─── Diff rendering ───

    private static bool IsDiffContent(string text)
    {
        var lines = text.Split('\n');
        var diffMarkers = lines.Count(l => l.StartsWith("--- ") || l.StartsWith("+++ ") || l.StartsWith("@@ "));
        return diffMarkers >= 2;
    }

    private void RenderDiffBlock(StackPanel panel, string diff)
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

    // ─── File preview (first N lines) ───

    private static string TruncateFilePreview(string content, string path, int maxLines = 10)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines) return content;
        var preview = string.Join("\n", lines.Take(maxLines));
        return $"{preview}\n\n... ({lines.Length - maxLines} more lines) — use read_file with range to see more";
    }

    private static List<(string Content, bool IsCode)> SplitCodeBlocks(string text) => ChatMessageRenderer.SplitCodeBlocks(text);

    private async Task RenderInlineImage(StackPanel panel, string path)
    {
        try
        {
            var isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            string localPath;
            if (isUrl)
            {
                var http = App.HttpFactory?.CreateClient() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var resp = await http.GetAsync(path);
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
        catch (Exception) { }
    }

    private async Task PickFilesAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose files to load",
            AllowMultiple = true
        });
        await ImportDroppedItems(files.ToList<IStorageItem>());
    }

    private async Task PickFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder to load",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        await ImportDroppedItems(folders.ToList<IStorageItem>());
    }

    private void AddBubble(string label, string text, Color accent, Color border)
    {
        var isUser = label == "[You]";
        var b = new Border
        {
            Background = LtaiTheme.Sbb(isUser ? LtaiTheme.BubbleUserBg : LtaiTheme.BubbleAIBg),
            BorderBrush = LtaiTheme.Sbb(isUser ? LtaiTheme.BubbleUserBorder : LtaiTheme.BubbleAIBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(12, 12, isUser ? 4 : 12, isUser ? 12 : 4),
            Padding = new(10),
            Margin = new(0, 4)
        };
        var s = new StackPanel();

        s.Children.Add(new TextBlock { Text = label, Foreground = LtaiTheme.Sbb(accent), FontWeight = FontWeight.Bold, FontSize = 11 });

        var stb = new SelectableTextBlock
        {
            Text = text,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnBubble),
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        s.Children.Add(stb);

        var copyRow = new DockPanel { Margin = new(0, 4, 0, 0) };
        var copyBtn = CopyButton(text);
        copyRow.Children.Add(copyBtn);
        s.Children.Add(copyRow);

        b.Child = s;
        _outputStack.Children.Add(b);
        PruneOutputStack();
    }

    private StackPanel AddAIBubbleHeader()
    {
        var b = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BubbleAIBg),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.BubbleAIBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(12, 12, 12, 4),
            Padding = new(10),
            Margin = new(0, 4)
        };
        var s = new StackPanel();

        var headerRow = new DockPanel();
        headerRow.Children.Add(new TextBlock { Text = "[LTAI]", Foreground = LtaiTheme.Sbb(LtaiTheme.ChatAI), FontWeight = FontWeight.Bold, FontSize = 11 });
        _aiBubbleStack = s;
        _aiBubbleBorder = b;
        s.Children.Add(headerRow);

        b.Child = s;
        _outputStack.Children.Add(b);
        PruneOutputStack();
        return s;
    }

    private StackPanel? _aiBubbleStack;
    private Border? _aiBubbleBorder;

    private void AddAICopyButton(string text)
    {
        if (_aiBubbleStack == null) return;
        var copyRow = new DockPanel { Margin = new(0, 6, 0, 0) };
        var copyBtn = CopyButton(text);
        copyRow.Children.Add(copyBtn);
        _aiBubbleStack.Children.Add(copyRow);
    }

    private void AddSuggestionCards()
    {
        var prompts = new[]
        {
            ("💡", "解释这段 C# 代码", "分析当前项目中的代码逻辑"),
            ("🔧", "帮我重构", "重构选中的方法或类"),
            ("📋", "写 Git 提交规范", "根据变更生成规范的提交信息"),
        };
        foreach (var (icon, title, desc) in prompts)
        {
            var card = new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BubbleAIBg),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.BubbleAIBorder),
                BorderThickness = new(1),
                CornerRadius = LtaiTheme.Radius.Md,
                Padding = new(12, 10),
                Margin = new(0, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock
            {
                Text = $"{icon}  {title}",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnBubble),
                FontWeight = FontWeight.Bold,
                FontSize = 13,
            });
            stack.Children.Add(new TextBlock
            {
                Text = desc,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextMuted),
                FontSize = 11,
            });
            card.Child = stack;
            card.PointerPressed += (_, _) =>
            {
                _input.Text = title;
                _input.CaretIndex = title.Length;
                _ = SendAsync();
            };
            _outputStack.Children.Add(card);
            PruneOutputStack();
        }
    }
}
