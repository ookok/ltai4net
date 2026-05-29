using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LTAI.Core.System;

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
            Watermark = "Type here... Enter=Send, Shift+Enter=newline, Ctrl+Enter=Send, Up/Down=history  |  Drag files/folders here",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            FontFamily = new("Consolas"),
            MinHeight = 72,
            AcceptsReturn = false,   // Enter sends; Shift+Enter inserts newline manually
            TextWrapping = TextWrapping.Wrap
        };
        _input.KeyDown += OnInputKey;

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

    private void OnInputKey(object? s, KeyEventArgs e)
    {
        // Shift+Enter → insert newline
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Shift)
        {
            var idx = _input.CaretIndex;
            _input.Text = _input.Text[..idx] + "\n" + _input.Text[idx..];
            _input.CaretIndex = idx + 1;
            e.Handled = true;
            return;
        }
        // Enter (plain or Ctrl) → send
        if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Control)
        {
            if (_isSending) return;
            e.Handled = true;
            _ = SendAsync().ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    System.Diagnostics.Debug.WriteLine($"SendAsync error: {t.Exception}");
            }, TaskScheduler.Default);
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
        if (_history.Count > 100) _history.RemoveRange(0, _history.Count - 100);
        _historyIdx = -1;
        _input.Text = "";
        _turns++;
        SetSending(true);

        AddBubble("[You]", query, LtaiTheme.ChatUser, LtaiTheme.Border);

        var aiBubble = AddAIBubbleHeader();
        var aiContent = new StackPanel { Spacing = 4 };
        aiBubble.Children.Add(aiContent);

        var statusDots = new TextBlock
        {
            Text = "⚪",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 16,
            Margin = new(4, 0)
        };
        var dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var dotFrames = new[] { "⚪", "⚫", "⚪" };
        var dotIdx = 0;
        dotTimer.Tick += (_, _) =>
        {
            dotIdx = (dotIdx + 1) % dotFrames.Length;
            Dispatcher.UIThread.Post(() => statusDots.Text = dotFrames[dotIdx]);
        };
        dotTimer.Start();
        aiContent.Children.Add(statusDots);

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

        var toolPanel = new StackPanel { Spacing = 2, IsVisible = false, Margin = new(0, 2) };
        var toolTitle = new TextBlock
        {
            Text = "Tools",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 10,
            FontStyle = FontStyle.Italic,
            Margin = new(0, 0, 0, 2)
        };
        toolPanel.Children.Add(toolTitle);
        aiContent.Children.Add(toolPanel);

        var responsePanel = new StackPanel { Spacing = 2 };
        aiContent.Children.Add(responsePanel);

        Border? taskBanner = null;
        var firstTokenReceived = false;

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
                    if (dotTimer.IsEnabled)
                    {
                        dotTimer.Stop();
                        Dispatcher.UIThread.Post(() => aiContent.Children.Remove(statusDots));
                    }

                    // Detect ReAct tool calls via 📋 emoji (U+1F4CB = "\uD83D\uDCCB")
                    // The emoji is a visual indicator that the next text is a tool invocation.
                    // We show a spinner in the tool panel and advance on each new 📋.
                    if (token.Contains("\uD83D\uDCCB"))
                    {
                        var toolName = token.Replace("\uD83D\uDCCB", "").Trim();
                        if (string.IsNullOrWhiteSpace(toolName)) toolName = "tool";
                        var currentToolName = toolName;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (taskBanner?.Child is TextBlock tbb)
                                tbb.Text = "⚡ Executing tools...";
                            toolPanel.IsVisible = true;

                            var toolRow = new DockPanel { Margin = new(0, 1) };
                            toolRow.Children.Add(new TextBlock
                            {
                                Text = $"🔧 {currentToolName}",
                                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                                FontFamily = new("Consolas"),
                                FontSize = 11
                            });

                            var progressBar = new ProgressBar
                            {
                                IsIndeterminate = true,
                                Width = 80,
                                Height = 4,
                                Margin = new(8, 0, 0, 0)
                            };
                            DockPanel.SetDock(progressBar, Dock.Right);
                            toolRow.Children.Add(progressBar);

                            toolPanel.Children.Add(toolRow);
                        });
                    }
                    else
                    {
                        if (!firstTokenReceived)
                        {
                            firstTokenReceived = true;
                            taskBanner = new Border
                            {
                                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                                CornerRadius = new(4),
                                Padding = new(6, 3),
                                Margin = new(0, 0, 0, 4),
                                Child = new TextBlock
                                {
                                    Text = "⚡ Processing...",
                                    Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                                    FontSize = 11
                                }
                            };
                            Dispatcher.UIThread.Post(() => aiContent.Children.Insert(0, taskBanner));
                        }
                        responseBuf.Append(token);
                        _tokens++;
                        if (_tokens % 8 == 0)
                            RenderResponse(responsePanel, responseBuf.ToString());
                    }
                }

                _scroller.ScrollToEnd();
                await Task.Yield();
            }

            RenderResponse(responsePanel, responseBuf.ToString());

            if (thinkPanel.IsVisible && thinkBuf.Length > 0 && thinkText.Text?.Length == 0)
                thinkText.Text = thinkBuf.ToString();

            if (taskBanner?.Child is TextBlock tb)
                Dispatcher.UIThread.Post(() => tb.Text = "✅ Complete");

            var aiFullText = responseBuf.ToString();
            var thinkCopy = thinkBuf.Length > 0 ? $"<thinking>\n{thinkBuf}\n</thinking>\n\n" : "";
            var fullCopy = thinkCopy + aiFullText;
            if (fullCopy.Length > 0)
                AddAICopyButton(fullCopy);

            NotificationService.Show("LTAI", "Response ready");
            RefreshStats();
        }
        catch (OperationCanceledException)
        {
            responseBuf.Append(" [cancelled]");
            RenderResponse(responsePanel, responseBuf.ToString());
            if (taskBanner?.Child is TextBlock tb)
                Dispatcher.UIThread.Post(() => tb.Text = "⏹ Stopped");
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

        var imageMatches = System.Text.RegularExpressions.Regex.Matches(raw, @"!\[.*?\]\(([^)]+)\)|@""([^""]+)""");
        foreach (System.Text.RegularExpressions.Match m in imageMatches)
        {
            var imgPath = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(imgPath))
                RenderInlineImage(panel, imgPath);
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
            var topLevel = TopLevel.GetTopLevel(btn);
            var clipboard = topLevel?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(content);
            else
                return;
            btn.Content = "Done";
            btn.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            await Task.Delay(1500);
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

    private async void RenderInlineImage(StackPanel panel, string path)
    {
        try
        {
            var isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            string localPath;
            if (isUrl)
            {
                // Download URL image to temp file
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var ext = ".png";
                // Try to guess extension from URL
                var urlPath = new Uri(path).AbsolutePath;
                var urlExt = Path.GetExtension(urlPath)?.ToLowerInvariant();
                if (urlExt is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
                    ext = urlExt;

                localPath = Path.Combine(Path.GetTempPath(), $"ltai_img_{Guid.NewGuid():N}{ext}");
                var bytes = await http.GetByteArrayAsync(path);
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
                var image = new Image
                {
                    Source = new Avalonia.Media.Imaging.Bitmap(localPath),
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
        catch { }
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
