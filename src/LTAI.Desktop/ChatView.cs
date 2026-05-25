using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class ChatView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBox _input;
    private readonly StackPanel _outputStack;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _stats;
    private readonly Button _actionBtn;
    private readonly List<string> _history = [];
    private int _historyIdx = -1;
    private CancellationTokenSource? _cts;
    private int _turns, _tokens;
    private bool _isSending;

    public ChatView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        _stats = new TextBlock { Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 12 };
        DockPanel.SetDock(_stats, Dock.Top);
        root.Children.Add(_stats);

        var modelHeader = new TextBlock
        {
            Text = "LTAI Chat",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontSize = 14,
            FontWeight = FontWeight.Bold
        };
        DockPanel.SetDock(modelHeader, Dock.Top);
        root.Children.Add(modelHeader);

        var inputBar = new DockPanel { Margin = new(0, 8) };

        var toolbox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var fileBtn = new Button
        {
            Content = "Files", Width = 52,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11
        };
        fileBtn.Click += async (_, _) => await PickFilesAsync();

        var folderBtn = new Button
        {
            Content = "Folder", Width = 55,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11
        };
        folderBtn.Click += async (_, _) => await PickFolderAsync();

        toolbox.Children.Add(fileBtn);
        toolbox.Children.Add(folderBtn);

        _input = new TextBox
        {
            Watermark = "Type here... Enter=Send, Shift+Enter=newline, Up/Down=history  |  Drag files/folders here",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            FontFamily = new("Consolas"),
            MinHeight = 72,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        _input.KeyDown += OnInputKey;
        _input.AddHandler(KeyDownEvent, OnInputKeyRaw, handledEventsToo: true);

        _actionBtn = new Button
        {
            Content = "Send",
            Width = 60,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontWeight = FontWeight.Bold
        };
        _actionBtn.Click += (_, _) =>
        {
            if (_isSending) Cancel();
            else _ = SendAsync();
        };

        var inputStack = new StackPanel { Spacing = 4 };
        inputStack.Children.Add(toolbox);
        inputStack.Children.Add(_input);
        DockPanel.SetDock(inputBar, Dock.Bottom);

        var btnPanel = new DockPanel { Margin = new(4, 0, 0, 0) };
        btnPanel.Children.Add(_actionBtn);
        DockPanel.SetDock(btnPanel, Dock.Right);
        inputBar.Children.Add(btnPanel);
        inputBar.Children.Add(inputStack);

        root.Children.Add(inputBar);

        _outputStack = new StackPanel { Spacing = 8 };
        _scroller = new ScrollViewer { Content = _outputStack };
        root.Children.Add(_scroller);

        SetupDragDrop();

        Content = root;

        void OnThemeChanged()
        {
            Background = LtaiTheme.Sbb(LtaiTheme.Bg);
            _stats.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
            _input.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
            _input.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
            _actionBtn.Background = _isSending
                ? LtaiTheme.Sbb(LtaiTheme.AccentDanger)
                : LtaiTheme.Sbb(LtaiTheme.AccentDNA);
        }

        LtaiTheme.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) => LtaiTheme.ThemeChanged -= OnThemeChanged;

        RefreshStats();
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void OnInputKeyRaw(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (_isSending) return;
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void OnInputKey(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (_isSending) return;
            e.Handled = true;
            _ = SendAsync();
        }
        else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && _history.Count > 0)
        {
            if (_historyIdx == -1) _historyIdx = _history.Count - 1;
            else if (_historyIdx > 0) _historyIdx--;
            _input.Text = _history[_historyIdx];
            _input.CaretIndex = _input.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None && _historyIdx >= 0)
        {
            _historyIdx++;
            _input.Text = _historyIdx < _history.Count ? _history[_historyIdx] : "";
            _input.CaretIndex = _input.Text.Length;
            if (_historyIdx >= _history.Count) _historyIdx = -1;
            e.Handled = true;
        }
    }

    private void SetupDragDrop()
    {
        DragDrop.SetAllowDrop(_input, true);

        _input.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.Data.Contains(DataFormats.Files))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        });

        _input.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (!e.Data.Contains(DataFormats.Files)) return;
            var items = e.Data.GetFiles()?.ToList();
            if (items == null || items.Count == 0) return;
            e.Handled = true;
            await ImportDroppedItems(items);
        });
    }

    private async Task ImportDroppedItems(List<IStorageItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            try
            {
                var path = item.Path.LocalPath;
                if (Directory.Exists(path))
                {
                    sb.AppendLine($"@@\"{path}\"");
                }
                else if (File.Exists(path))
                {
                    var content = await File.ReadAllTextAsync(path);
                    var snippet = content.Length > 2000 ? content[..2000] + "\n...(truncated)" : content;
                    var name = Path.GetFileName(path);
                    sb.AppendLine($"@\"{path}\"");
                    sb.AppendLine($"{name}:");
                    sb.AppendLine(snippet);
                }
            }
            catch { }
        }
        if (sb.Length > 0)
        {
            if (_input.Text?.Length > 0) _input.Text += "\n";
            _input.Text += sb.ToString();
        }
    }

    private void SetSending(bool sending)
    {
        _isSending = sending;
        _actionBtn.Content = sending ? "Stop" : "Send";
        _actionBtn.Background = sending
            ? LtaiTheme.Sbb(LtaiTheme.AccentDanger)
            : LtaiTheme.Sbb(LtaiTheme.AccentDNA);
    }

    private async Task SendAsync()
    {
        var query = _input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        _history.Add(query);
        _historyIdx = -1;
        _input.Text = "";
        _turns++;
        SetSending(true);

        AddBubble("[You]", query, LtaiTheme.ChatUser, LtaiTheme.Border);

        var aiBubble = AddAIBubbleHeader();
        var aiContent = new StackPanel { Spacing = 4 };
        aiBubble.Children.Add(aiContent);

        var thinkPanel = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.ThinkBg),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            CornerRadius = new(4),
            Padding = new(6),
            Margin = new(0, 2),
            IsVisible = false
        };
        var thinkText = new SelectableTextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = new("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var thinkInner = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Thinking", Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 10, FontStyle = FontStyle.Italic },
                thinkText
            }
        };
        thinkPanel.Child = thinkInner;
        aiContent.Children.Add(thinkPanel);

        var responsePanel = new StackPanel { Spacing = 2 };
        aiContent.Children.Add(responsePanel);

        _cts = new CancellationTokenSource();
        var responseBuf = new StringBuilder();
        var thinkBuf = new StringBuilder();
        var inThinking = false;

        try
        {
            await foreach (var token in _svc.LTS.StreamChatAsync(query).WithCancellation(_cts.Token))
            {
                _tokens++;

                if (token.StartsWith("<thinking>"))
                {
                    inThinking = true;
                    thinkBuf.Append(token.AsSpan("<thinking>".Length));
                }
                else if (token.EndsWith("</thinking>"))
                {
                    thinkBuf.Append(token.AsSpan(0, token.Length - "</thinking>".Length));
                    inThinking = false;
                    thinkPanel.IsVisible = true;
                    thinkText.Text = thinkBuf.ToString();
                }
                else if (inThinking)
                {
                    thinkBuf.Append(token);
                    thinkText.Text = thinkBuf.ToString();
                }
                else
                {
                    responseBuf.Append(token);
                    RenderResponse(responsePanel, responseBuf.ToString());
                }

                _scroller.ScrollToEnd();
                await Task.Yield();
            }

            RenderResponse(responsePanel, responseBuf.ToString());

            if (thinkPanel.IsVisible && thinkBuf.Length > 0 && thinkText.Text?.Length == 0)
                thinkText.Text = thinkBuf.ToString();

            var aiFullText = responseBuf.ToString();
            var thinkCopy = thinkBuf.Length > 0 ? $"<thinking>\n{thinkBuf}\n</thinking>\n\n" : "";
            var fullCopy = thinkCopy + aiFullText;
            if (fullCopy.Length > 0)
                AddAICopyButton(fullCopy);

            RefreshStats();
        }
        catch (OperationCanceledException)
        {
            responseBuf.Append(" [cancelled]");
            RenderResponse(responsePanel, responseBuf.ToString());
            AddAICopyButton(responseBuf.ToString());
        }
        catch (Exception ex)
        {
            responseBuf.Append($"\n[Error] {ex.Message}");
            RenderResponse(responsePanel, responseBuf.ToString());
            AddAICopyButton(responseBuf.ToString());
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetSending(false);
        }
    }

    private void RenderResponse(StackPanel panel, string raw)
    {
        panel.Children.Clear();

        var cleaned = CleanResponse(raw);
        var parts = SplitCodeBlocks(cleaned);

        foreach (var part in parts)
        {
            if (part.IsCode)
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
                codeBorder.Child = new SelectableTextBlock
                {
                    Text = part.Content,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    FontFamily = new("Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                codeRow.Children.Add(codeBorder);

                var copyBtn = CopyButton(part.Content);
                DockPanel.SetDock(copyBtn, Dock.Right);
                copyBtn.HorizontalAlignment = HorizontalAlignment.Right;
                copyBtn.VerticalAlignment = VerticalAlignment.Top;
                copyBtn.Margin = new(4, 0, 0, 0);
                codeRow.Children.Add(copyBtn);

                panel.Children.Add(codeRow);
            }
            else
            {
                var stb = new SelectableTextBlock
                {
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                };
                MarkdownRenderer.Render(part.Content, stb.Inlines!);
                panel.Children.Add(stb);
            }
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
        btn.Click += async (_, _) =>
        {
            await TopLevel.GetTopLevel(btn)!.Clipboard!.SetTextAsync(content);
            btn.Content = "Done";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            await Task.Delay(1500);
            btn.Content = "Copy";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        };
        return btn;
    }

    private static string CleanResponse(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '\uD83D') continue;
            sb.Append(ch);
        }
        var result = sb.ToString();
        result = result.Replace("\uDD0D", "").Replace("\uDCCB", "");
        return result;
    }

    private static List<(string Content, bool IsCode)> SplitCodeBlocks(string text)
    {
        var parts = new List<(string, bool)>();
        var fence = "```";
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
        var b = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(border),
            BorderThickness = new(1),
            CornerRadius = new(6),
            Padding = new(10),
            Margin = new(0, 4)
        };
        var s = new StackPanel();

        s.Children.Add(new TextBlock { Text = label, Foreground = LtaiTheme.Sbb(accent), FontWeight = FontWeight.Bold, FontSize = 11 });

        var stb = new SelectableTextBlock
        {
            Text = text,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
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
    }

    private StackPanel AddAIBubbleHeader()
    {
        var b = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            BorderThickness = new(1),
            CornerRadius = new(6),
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

    private void RefreshStats()
    {
        _stats.Text = string.Format("Turns: {0} | Tokens: {1} | Model: {2}", _turns, _tokens, _svc.LTS.Mode);
    }
}
