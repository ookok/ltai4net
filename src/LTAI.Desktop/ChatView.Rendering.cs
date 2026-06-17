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
        => ChatMessageRenderer.UpdateResponseText(panel, ref _currentResponseText, text);

    private static Button CopyButton(string content) => ChatMessageRenderer.CopyButton(content);

    private void RenderDiffBlock(StackPanel panel, string diff) => ChatMessageRenderer.RenderDiffBlock(panel, diff);

    private static string TruncateFilePreview(string content, string path, int maxLines = 10)
        => ChatMessageRenderer.TruncateFilePreview(content, path, maxLines);

    private static List<(string Content, bool IsCode)> SplitCodeBlocks(string text) => ChatMessageRenderer.SplitCodeBlocks(text);

    private Task RenderInlineImage(StackPanel panel, string path) => ChatMessageRenderer.RenderInlineImage(panel, path);

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
            CornerRadius = new CornerRadius(14, 14, isUser ? 4 : 14, isUser ? 14 : 4),
            Padding = new(12, 8),
            Margin = new(isUser ? 60 : 0, 4, isUser ? 0 : 60, 4),
        };
        var s = new StackPanel();

        s.Children.Add(new TextBlock { Text = label, Foreground = LtaiTheme.Sbb(accent), FontSize = 11 });

        var stb = new SelectableTextBlock
        {
            Text = text,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnBubble),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        s.Children.Add(stb);

        b.Child = s;
        _outputStack.Children.Add(b);
    }

    private StackPanel AddAIBubbleHeader()
    {
        var b = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BubbleAIBg),
            CornerRadius = new CornerRadius(14, 14, 14, 4),
            Padding = new(12, 8),
            Margin = new(0, 4, 60, 4),
        };
        var s = new StackPanel();
        var headerRow = new DockPanel();
        headerRow.Children.Add(new TextBlock { Text = "[LTAI]", Foreground = LtaiTheme.Sbb(LtaiTheme.ChatAI), FontSize = 11 });
        _aiBubbleStack = s;
        _aiBubbleBorder = b;
        s.Children.Add(headerRow);

        b.Child = s;
        _outputStack.Children.Add(b);
        return s;
    }

    private StackPanel? _aiBubbleStack;
    private Border? _aiBubbleBorder;

    private void AddAICopyButton(string text)
    {
        if (_aiBubbleStack == null || string.IsNullOrWhiteSpace(text)) return;
        var copyBtn = new Button
        {
            Content = "📋 复制",
            FontSize = 11,
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        copyBtn.Click += async (_, _) =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(text).ConfigureAwait(false);
                    copyBtn.Content = "✅ 已复制";
                    await Task.Delay(1500).ConfigureAwait(false);
                    copyBtn.Content = "📋 复制";
                }
            }
            catch
            {
                copyBtn.Content = "❌ 复制失败";
            }
        };
        _aiBubbleStack.Children.Add(copyBtn);
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
        }
    }
}
