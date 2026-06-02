using System.Diagnostics;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Reflection;

namespace LTAI.Desktop;

public sealed class ChatView : UserControl
{
    // 共享 HttpClient — 复用连接池，避免 socket 耗尽
    private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static bool _httpDisposed;

    private readonly LTAIService _svc;
    private readonly TextBox _input;
    private readonly StackPanel _outputStack;
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _footerStats;
    private readonly Button _actionBtn;
    private readonly List<string> _history = [];
    private int _historyIdx = -1;
    private CancellationTokenSource? _cts;
    private int _turns, _tokens;
    private bool _isSending;
    private readonly SessionManager _sessionManager;
    private readonly LTAI.Desktop.ToolRendering.ToolResultRendererRegistry _toolRenderers;
    private readonly LTAI.Agent.Snippets.SnippetStore? _snippetStore;
    private TextBlock? _currentResponseText;

    private readonly Dictionary<int, string> _subSessions = new();
    private readonly Dictionary<int, Stopwatch> _subStartTimes = new();

    public SessionManager SessionManager => _sessionManager;

    public async Task LoadSessionAsync(string name)
    {
        if (!await _sessionManager.LoadSessionAsync(name)) return;
        _outputStack.Children.Clear();

        // 子会话显示返回父会话按钮
        var sessions = _sessionManager.ListSessions();
        var sessionInfo = sessions.FirstOrDefault(s => s.Name == name);
        if (sessionInfo?.ParentId != null)
        {
            var parentName = sessionInfo.ParentId;
            var backBtn = new Button
            {
                Content = "🔙 返回父会话",
                FontSize = 11, Height = 22,
                Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                Foreground = LtaiTheme.Sbb("#ffffff"),
                BorderThickness = new(0), CornerRadius = new(4),
                Margin = new(0, 4),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            backBtn.Click += async (_, _) => await LoadSessionAsync(parentName);
            _outputStack.Children.Add(new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                BorderThickness = new(1), CornerRadius = new(6),
                Padding = new(8), Margin = new(0, 0, 0, 6),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "📋 子任务详情", FontWeight = FontWeight.Bold, FontSize = 12, Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo) },
                        new TextBlock { Text = "子 Agent 的完整对话记录", FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary) },
                        backBtn
                    }
                }
            });
        }

        foreach (var msg in _sessionManager.Messages)
        {
            var label = msg.Role == ChatRole.User ? "[You]" : "[LTAI]";
            var color = msg.Role == ChatRole.User ? LtaiTheme.ChatUser : LtaiTheme.AccentSystem;
            AddBubble(label, msg.Text ?? "", color, LtaiTheme.Border);
        }
        _turns = _sessionManager.Messages.Count / 2;
        RefreshStats();
    }

    public async Task ResetSessionAsync()
    {
        if (_sessionManager.MessageCount > 0)
            await _sessionManager.SaveSessionAsync().ConfigureAwait(false);
        _sessionManager.NewSession();
        _outputStack.Children.Clear();
        _turns = 0;
        _tokens = 0;
        RefreshStats();
        AddSystemBubble("✅ 新会话已创建");
    }

    /// <summary>从外部设置输入文本并自动发送（用于"问 AI"功能）。</summary>
    public async Task SendContentAsync(string text)
    {
        _input.Text = text;
        await SendAsync();
    }

    public ChatView(LTAIService svc, SessionManager? sessionManager = null)
    {
        _svc = svc;
        _sessionManager = sessionManager ?? new SessionManager();
        _toolRenderers = LTAI.Desktop.ToolRendering.DefaultRenderers.Create();
        // Resolve SnippetStore from DI (if available). null-safe — Desktop must
        // still function when launched in degraded mode without LTAI.Agent DI.
        _snippetStore = App.Services?.GetService(typeof(LTAI.Agent.Snippets.SnippetStore))
            as LTAI.Agent.Snippets.SnippetStore;
        // P17.5: subscribe to LLM questions
        SetupQuestionHandler();
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var modelHeader = new TextBlock
        {
            Text = "LTAI Chat",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontSize = 14,
            FontWeight = FontWeight.Bold
        };
        DockPanel.SetDock(modelHeader, Dock.Top);
        root.Children.Add(modelHeader);

        // ── Footer (multi-line stats + input bar) ──
        _footerStats = new StackPanel { Spacing = 1 };
        var footerBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1, 0, 0, 0),
            Padding = new(0, 6, 0, 0),
        };
        var footerStack = new StackPanel();
        footerStack.Children.Add(_footerStats);

        var toolbox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new(0, 6, 0, 0)
        };
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
            PlaceholderText = "输入消息... Enter=发送, Shift+Enter=换行, Ctrl+Enter=发送, ↑↓=历史  |  拖入文件/文件夹",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            FontFamily = new("Consolas"),
            MinHeight = 72,
            AcceptsReturn = false,
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

        var inputRow = new DockPanel { Margin = new(0, 4, 0, 0) };
        var btnPanel = new DockPanel { Margin = new(4, 0, 0, 0) };
        btnPanel.Children.Add(_actionBtn);
        DockPanel.SetDock(btnPanel, Dock.Right);
        inputRow.Children.Add(btnPanel);
        inputRow.Children.Add(_input);

        footerStack.Children.Add(toolbox);
        footerStack.Children.Add(inputRow);
        footerBorder.Child = footerStack;
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);

        // ── Messages area ──
        _outputStack = new StackPanel { Spacing = 8 };
        _scroller = new ScrollViewer { Content = _outputStack };
        root.Children.Add(_scroller);

        // Auto-load most recent session or start fresh
        var existing = _sessionManager.ListSessions();
        if (existing.Length > 0 && _sessionManager.LoadSession(existing[0].Name))
        {
            foreach (var msg in _sessionManager.Messages)
            {
                var label = msg.Role == ChatRole.User ? "[You]" : "[LTAI]";
                var color = msg.Role == ChatRole.User ? LtaiTheme.ChatUser : LtaiTheme.AccentSystem;
                AddBubble(label, msg.Text ?? "", color, LtaiTheme.Border);
            }
            _turns = _sessionManager.Messages.Count / 2;
        }
        else
        {
            _sessionManager.NewSession();
        }

        SetupDragDrop();
        Content = root;

        void OnThemeChanged()
        {
            Background = LtaiTheme.Sbb(LtaiTheme.Bg);
            _input.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
            _input.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
            _actionBtn.Background = _isSending
                ? LtaiTheme.Sbb(LtaiTheme.AccentDanger)
                : LtaiTheme.Sbb(LtaiTheme.AccentDNA);
        }

        LtaiTheme.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            LtaiTheme.ThemeChanged -= OnThemeChanged;
            LTAI.Agent.Tools.SubagentTools.OnSubagentMessage -= OnSubagentMessage;
            LTAI.Agent.Tools.SubagentTools.OnSubagentComplete -= OnSubagentComplete;
            if (_questionHandler != null)
            {
                var qs = App.Services?.GetService(typeof(LTAI.Agent.Tools.QuestionService))
                    as LTAI.Agent.Tools.QuestionService;
                if (qs != null) qs.QuestionPosted -= _questionHandler;
            }
        };
        LTAI.Agent.Tools.SubagentTools.OnSubagentMessage += OnSubagentMessage;
        LTAI.Agent.Tools.SubagentTools.OnSubagentComplete += OnSubagentComplete;

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
            var idx = _input!.CaretIndex;
            var text = _input!.Text ?? "";
            _input!.Text = text[..idx] + "\n" + text[idx..];
            _input!.CaretIndex = idx + 1;
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
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        });

        // Avalonia 12.0: Access drag data via DataObject.GetDataFromDragDropEvent
        _input.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            try
            {
                // Avalonia 12.0: DragEventArgs doesn't expose Data directly.
                // Use DataObject.TryGetDataFromDropEvent or reflection fallback.
                var data = await GetDragDropDataAsync(e);
                if (data is IEnumerable<IStorageItem> files)
                {
                    e.Handled = true;
                    await ImportDroppedItems(files.ToList());
                }
            }
            catch { /* drag data unavailable */ }
        });
    }

    private static async Task<object?> GetDragDropDataAsync(DragEventArgs e)
    {
        // Avalonia 12.0: DragEventArgs.Data removed. Access via reflection.
        try
        {
            // Try common property names used across Avalonia 12.x versions
            foreach (var propName in new[] { "DataObject", "Data", "DragData" })
            {
                var prop = e.GetType().GetProperty(propName,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var val = prop?.GetValue(e);
                if (val == null) continue;

                // If it's already an IStorageItem enumerable
                if (val is IEnumerable<IStorageItem> items)
                    return items.ToList();

                // If it has GetFiles method (DataObject pattern)
                var getFiles = val.GetType().GetMethod("GetFiles");
                if (getFiles != null)
                {
                    var files = getFiles.Invoke(val, null);
                    if (files is IEnumerable<IStorageItem> storageItems)
                        return storageItems.ToList();
                }

                // If it has GetDataAsync (async DataObject pattern)
                var getDataAsync = val.GetType().GetMethod("GetDataAsync", [typeof(string)]);
                if (getDataAsync != null)
                {
                    var task = (Task)getDataAsync.Invoke(val, [DataFormat.File])!;
                    await task.ConfigureAwait(false);
                    var result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (result is IEnumerable<IStorageItem> asyncItems)
                        return asyncItems.ToList();
                }
            }
        }
        catch { /* data not accessible */ }
        return null;
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
            catch { /* user cancelled — OK */ }
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

        // Handle slash commands
        if (query.StartsWith('/'))
        {
            _input.Text = "";
            TryHandleSlashCommand(query);
            return;
        }

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
        _currentResponseText = null;

        Border? taskBanner = null;
        var firstTokenReceived = false;

        _cts = new CancellationTokenSource();
        var responseBuf = new StringBuilder();
        var thinkBuf = new StringBuilder();
        var inThinking = false;
        var lastRenderedText = "";
        var lastUiUpdate = DateTime.UtcNow;
        const int uiThrottleMs = 20;

        try
        {
            await foreach (var update in _svc.Chat.ChatStreamingAsync(query, ct: _cts.Token))
            {
                var token = update.Text ?? "";
                _tokens++;

                // Check tool result renderer registry first
                var rendered = _toolRenderers.Render(token);
                if (rendered != null)
                {
                    statusDots.Text = "⚡";
                    responseBuf.Append($" {Truncate(token, 80)}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        toolPanel.IsVisible = true;
                        toolPanel.Children.Add(rendered);
                    });
                    continue;
                }

                // Thinking tags
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
                    if (_tokens % 8 == 0 && (DateTime.UtcNow - lastUiUpdate).TotalMilliseconds >= uiThrottleMs)
                    {
                        lastUiUpdate = DateTime.UtcNow;
                        var text = responseBuf.ToString();
                        if (text != lastRenderedText)
                        {
                            lastRenderedText = text;
                            UpdateResponseText(responsePanel, text);
                        }
                    }
                }

                _scroller.ScrollToEnd();
                if (_tokens % 20 == 0)
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

            // Plan detection: if response contains a plan, add approve button
            if (aiFullText.Contains("## Plan:") || aiFullText.Contains("approve"))
            {
                var planStatus = LTAI.Agent.Tools.PlanTools.PlanStatus();
                if (!planStatus.Contains("No active plan"))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var approveBtn = new Button
                        {
                            Content = "✅ Approve Plan",
                            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
                            Foreground = LtaiTheme.Sbb("#ffffff"),
                            FontWeight = FontWeight.Bold,
                            Margin = new(0, 4, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        approveBtn.Click += (_, _) =>
                        {
                            var result = LTAI.Agent.Tools.PlanTools.ApprovePlan()
                                       + "\n"
                                       + LTAI.Agent.Tools.PlanTools.StartExecution();
                            var statusBlock = new TextBlock
                            {
                                Text = result,
                                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap
                            };
                            responsePanel.Children.Add(statusBlock);
                            approveBtn.IsEnabled = false;
                            approveBtn.Content = "✅ Approved";
                        };
                        responsePanel.Children.Add(approveBtn);
                    });
                }
            }

            // Save session after each completed turn
            _sessionManager.AddMessage(ChatRole.User, query);
            _sessionManager.AddMessage(ChatRole.Assistant, responseBuf.ToString());
            await _sessionManager.SaveSessionAsync().ConfigureAwait(false);

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
            if (dotTimer.IsEnabled) dotTimer.Stop();
            SetSending(false);
        }
    }

    private void RenderResponse(StackPanel panel, string raw)
    {
        panel.Children.Clear();

        var cleaned = CleanResponse(raw);

        // Detect diff blocks: ---/+++/@@ pattern
        if (IsDiffContent(cleaned))
        {
            RenderDiffBlock(panel, cleaned);
            return;
        }

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
                // Syntax-highlighted code block
                // Line-by-line rendering with gutter line numbers
                var codeStack = new StackPanel();
                var lang = "csharp";
                var keywords = MarkdownRenderer.GetKeywords(lang);
                var codeLines = part.Content.Split('\n');
                var linePad = codeLines.Length.ToString().Length;
                var maxLines = 50;
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
                        TextAlignment = Avalonia.Media.TextAlignment.Right,
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
                _ = RenderInlineImage(panel, imgPath);
        }
    }

    private void UpdateResponseText(StackPanel panel, string text)
    {
        if (_currentResponseText == null)
        {
            _currentResponseText = new TextBlock
            {
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
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
        btn.DetachedFromVisualTree += (_, _) => cts.Cancel();
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
                color = Color.Parse("#4CAF50"); // green
                prefix = "+";
            }
            else if (line.StartsWith("-") && !line.StartsWith("---"))
            {
                color = Color.Parse("#F44336"); // red
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

    // ─── File preview (first N lines) ───

    private static string TruncateFilePreview(string content, string path, int maxLines = 10)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines) return content;
        var preview = string.Join("\n", lines.Take(maxLines));
        return $"{preview}\n\n... ({lines.Length - maxLines} more lines) — use read_file with range to see more";
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

    private async Task RenderInlineImage(StackPanel panel, string path)
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
            System.Diagnostics.Debug.WriteLine($"ChatView: Failed to render inline image: {ex.Message}");
        }
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

    // ── 命令处理 ──

    private static readonly string[] KnownCommands = [
        "help", "new", "exit", "status", "pwd", "plan", "approve",
        "ls", "cd", "config", "model", "cost", "retry", "compact",
        "memory", "skill", "mode", "undo", "monitor", "snippet"
    ];

    private bool TryHandleSlashCommand(string input)
    {
        var parts = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmdName = parts[0][1..].ToLowerInvariant();
        if (string.IsNullOrEmpty(cmdName)) cmdName = "help";
        var args = parts.Length > 1 ? parts[1] : "";

        switch (cmdName)
        {
            case "help":
            case "?":
            case "帮助":
                ShowHelp();
                return true;

            case "new":
            case "reset":
            case "clear":
            case "新":
            case "新建":
                if (_sessionManager.MessageCount > 0)
                    _sessionManager.SaveSession();
                _outputStack.Children.Clear();
                _sessionManager.NewSession();
                _turns = 0;
                _tokens = 0;
                RefreshStats();
                AddSystemBubble("✅ 会话已重置");
                return true;

            case "exit":
            case "quit":
            case "q":
            case "退出":
                (TopLevel.GetTopLevel(this) as Window)?.Close();
                return true;

            case "status":
            case "状态":
            case "统计":
                ShowStatus();
                return true;

            case "pwd":
            case "目录":
                AddSystemBubble($"📁 {Directory.GetCurrentDirectory()}");
                return true;

            case "plan":
            case "计划状态":
                AddSystemBubble(LTAI.Agent.Tools.PlanTools.PlanStatus());
                return true;

            case "approve":
            case "yes":
            case "confirm":
            case "批准":
            case "确认":
                AddSystemBubble(LTAI.Agent.Tools.PlanTools.ApprovePlan()
                    + "\n" + LTAI.Agent.Tools.PlanTools.StartExecution());
                return true;

            case "ls":
            case "dir":
            case "列表":
                var dir = Directory.GetCurrentDirectory();
                var entries = Directory.GetFileSystemEntries(dir)
                    .Take(30)
                    .Select(e =>
                    {
                        var name = Path.GetFileName(e);
                        return Directory.Exists(e) ? $"📁 {name}" : $"📄 {name}";
                    });
                AddSystemBubble($"📂 {dir}\n" + string.Join("\n", entries));
                return true;

            case "cd":
                if (string.IsNullOrWhiteSpace(args))
                {
                    AddSystemBubble("用法: /cd <目录路径>");
                    return true;
                }
                try
                {
                    Directory.SetCurrentDirectory(args);
                    AddSystemBubble($"📂 {Directory.GetCurrentDirectory()}");
                }
                catch (Exception ex)
                {
                    AddSystemBubble($"❌ {ex.Message}");
                }
                return true;

            case "snippet":
            case "snip":
            case "常用语":
            case "常用":
                HandleSnippetCommand(args);
                return true;

            default:
                string? bestName = null;
                var bestDist = int.MaxValue;
                for (int i = 0; i < KnownCommands.Length; i++)
                {
                    var d = Levenshtein(cmdName, KnownCommands[i]);
                    if (d <= 3 && d < bestDist)
                    {
                        bestDist = d;
                        bestName = KnownCommands[i];
                    }
                }
                var msg = bestName != null
                    ? $"⚠️ 未知命令 '/{cmdName}'。您是不是想输入 '/{bestName}'？"
                    : $"⚠️ 未知命令 '/{cmdName}'。输入 /help 查看可用命令。";
                AddSystemBubble(msg);
                return true;
        }
    }

    private void ShowHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("可用命令：");
        sb.AppendLine();
        sb.AppendLine("/help, /?     — 显示此帮助");
        sb.AppendLine("/new, /clear  — 新建会话");
        sb.AppendLine("/exit, /quit  — 退出应用");
        sb.AppendLine("/status       — 显示统计信息");
        sb.AppendLine("/pwd          — 显示当前目录");
        sb.AppendLine("/ls           — 列出当前目录");
        sb.AppendLine("/cd <路径>    — 切换工作目录");
        sb.AppendLine("/plan         — 查看计划状态");
        sb.AppendLine("/approve      — 批准当前计划");
        sb.AppendLine("/snippet      — 常用语: list|save <key> <text>|use <key>|delete <key>|rename|edit");
        AddSystemBubble(sb.ToString().TrimEnd());
    }

    // ── /snippet — 常用语管理 ──

    private void HandleSnippetCommand(string args)
    {
        if (_snippetStore == null)
        {
            AddSystemBubble("⚠️ 常用语存储未初始化（需要 LTAI.Agent 服务）");
            return;
        }

        // Fallback: /snippet <key> → use <key>
        var cmd = LTAI.Agent.Snippets.SnippetCommandParser.Parse(args);
        if (cmd.Action == LTAI.Agent.Snippets.SnippetAction.Unknown)
        {
            var firstToken = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(firstToken))
            {
                var existing = _snippetStore.GetAsync(firstToken).GetAwaiter().GetResult();
                if (existing != null)
                    cmd = new LTAI.Agent.Snippets.SnippetCommand(
                        LTAI.Agent.Snippets.SnippetAction.Use, firstToken, "", "", null);
            }
        }

        if (cmd.Error != null) { AddSystemBubble($"⚠️ {cmd.Error}"); return; }

        switch (cmd.Action)
        {
            case LTAI.Agent.Snippets.SnippetAction.List:
                ShowSnippetList();
                break;
            case LTAI.Agent.Snippets.SnippetAction.Save:
                TrySaveSnippet(cmd.Key, cmd.Content);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Use:
                TryUseSnippet(cmd.Key);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Delete:
                TryDeleteSnippet(cmd.Key);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Rename:
                TryRenameSnippet(cmd.Key, cmd.NewKey);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Edit:
                TrySaveSnippet(cmd.Key, cmd.Content);
                break;
        }
    }

    private async Task<LTAI.Agent.Snippets.Snippet?> TryGetSnippetAsync(string key)
        => _snippetStore == null ? null : await _snippetStore.GetAsync(key).ConfigureAwait(false);

    private async void ShowSnippetList()
    {
        try
        {
            if (_snippetStore == null) return;
            var list = await _snippetStore.ListAsync().ConfigureAwait(false);
            if (list.Count == 0)
            {
                AddSystemBubble("📝 暂无常用语\n用法: /snippet save <key> <text>");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"📝 常用语 ({list.Count} 条):");
            foreach (var s in list)
            {
                var lastUsed = s.LastUsedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "从未";
                var desc = string.IsNullOrEmpty(s.Description) ? "" : $"  — {s.Description}";
                var preview = s.Content.Length > 30 ? s.Content[..30] + "..." : s.Content;
                sb.AppendLine($"  /{s.Key,-16}  {preview,-34}  使用:{s.UseCount,3}  {lastUsed}{desc}");
            }
            sb.AppendLine("\n用法: /snippet use <key>");
            AddSystemBubble(sb.ToString().TrimEnd());
        }
        catch (Exception ex) { AddSystemBubble($"❌ 错误: {ex.Message}"); }
    }

    private async void TrySaveSnippet(string key, string content)
    {
        try
        {
            if (_snippetStore == null) return;
            await _snippetStore.SaveAsync(new LTAI.Agent.Snippets.Snippet
            {
                Key = key,
                Content = content,
            }).ConfigureAwait(false);
            AddSystemBubble($"✅ 已保存常用语 /{key}（{content.Length} 字符）");
        }
        catch (Exception ex)
        {
            AddSystemBubble($"❌ {ex.Message}");
        }
    }

    private async void TryUseSnippet(string key)
    {
        try
        {
            if (_snippetStore == null) return;
            var snippet = await _snippetStore.GetAsync(key).ConfigureAwait(false);
            if (snippet == null)
            {
                AddSystemBubble($"❌ 找不到常用语 '/{key}'");
                return;
            }
            await _snippetStore.TouchAsync(key).ConfigureAwait(false);
            // D61: fill the input box (not auto-send)
            _input.Text = snippet.Content;
            _input.CaretIndex = snippet.Content.Length;
            AddSystemBubble($"✅ 已调出常用语 /{key}（{snippet.Content.Length} 字符）— 已填入输入框");
        }
        catch (Exception ex) { AddSystemBubble($"❌ 错误: {ex.Message}"); }
    }

    private async void TryDeleteSnippet(string key)
    {
        try
        {
            if (_snippetStore == null) return;
            var existing = await _snippetStore.GetAsync(key).ConfigureAwait(false);
            if (existing == null)
            {
                AddSystemBubble($"❌ 找不到常用语 '/{key}'");
                return;
            }
            var usedHint = existing.UseCount > 0 ? $"（已使用 {existing.UseCount} 次）" : "";
            var ok = await _snippetStore.DeleteAsync(key).ConfigureAwait(false);
            AddSystemBubble(ok ? $"✅ 已删除常用语 /{key} {usedHint}" : $"❌ 删除失败");
        }
        catch (Exception ex) { AddSystemBubble($"❌ 错误: {ex.Message}"); }
    }

    private async void TryRenameSnippet(string oldKey, string newKey)
    {
        try
        {
            if (_snippetStore == null) return;
            var ok = await _snippetStore.RenameAsync(oldKey, newKey).ConfigureAwait(false);
            AddSystemBubble(ok
                ? $"✅ 已重命名 /{oldKey} → /{newKey}"
                : $"❌ 找不到常用语 '/{oldKey}'");
        }
        catch (Exception ex)
        {
            AddSystemBubble($"❌ {ex.Message}");
        }
    }

    private void ShowStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"回合: {_turns}");
        sb.AppendLine($"Token: {_tokens:N0}");
        sb.AppendLine($"模型: {_svc.Mode}");
        sb.AppendLine($"目录: {Directory.GetCurrentDirectory()}");
        AddSystemBubble(sb.ToString().TrimEnd());
    }

    private void AddSystemBubble(string text)
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
        var stb = new SelectableTextBlock
        {
            Text = text,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        b.Child = stb;
        _outputStack.Children.Add(b);
        _scroller.ScrollToEnd();
    }

    private static int Levenshtein(string a, string b)
    {
        var m = a.Length; var n = b.Length;
        var d = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = j;
        for (int j = 1; j <= n; j++)
            for (int i = 1; i <= m; i++)
                d[i, j] = a[i - 1] == b[j - 1]
                    ? d[i - 1, j - 1]
                    : Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + 1);
        return d[m, n];
    }

    private void RefreshStats()
    {
        _footerStats.Children.Clear();
        var dim = new SolidColorBrush(LtaiTheme.TextDim);

        TextBlock Line(string text) => new()
        {
            Text = text,
            Foreground = dim,
            FontSize = 11,
            FontFamily = new("Consolas"),
            TextWrapping = TextWrapping.NoWrap
        };

        var r = LTAI.Core.Configuration.UsageTracker.Requests;

        if (r > 0)
        {
            var l = $"模型: {LTAI.Core.Configuration.UsageTracker.ActiveModel}  " +
                    $"Token: {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}  " +
                    $"费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}";
            var tps = LTAI.Core.Configuration.UsageTracker.CurrentTps;
            if (tps.HasValue) l += $"  速率: {tps:F0} t/s";
            l += $"  请求: {r}";
            _footerStats.Children.Add(Line(l));

            var l2 = $"缓存: {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%  " +
                     $"余额: {LTAI.Core.Configuration.UsageTracker.BalanceDisplay}";
            var tc = LTAI.Core.Configuration.UsageTracker.ToolCalls;
            if (tc > 0) l2 += $"  工具: {tc}次";
            var saved = LTAI.Core.Configuration.UsageTracker.CacheSavedDisplay;
            if (saved != "¥0.0000") l2 += $"  节省: {saved}";
            _footerStats.Children.Add(Line(l2));

            var llmTime = LTAI.Core.Configuration.UsageTracker.LlmCallTimeDisplay;
            var toolTime = LTAI.Core.Configuration.UsageTracker.ToolCallTimeDisplay;
            if (!string.IsNullOrEmpty(llmTime) || !string.IsNullOrEmpty(toolTime))
            {
                var timing = new List<string>();
                if (!string.IsNullOrEmpty(llmTime)) timing.Add($"LLM: {llmTime}");
                if (!string.IsNullOrEmpty(toolTime)) timing.Add($"工具: {toolTime}");
                _footerStats.Children.Add(Line(string.Join("  ", timing)));
            }
        }
        else
        {
            _footerStats.Children.Add(Line("等待首次请求...  输入消息开始对话"));
        }
    }

    private static bool TryParseToolResult(string text, out (bool success, string output, string error) result)
    {
        result = default;
        text = text.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}')) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var s)) return false;
            var ok = s.GetBoolean();
            var output = ok && root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            var err = !ok && root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            result = (ok, output, err);
            return true;
        }
        catch { return false; }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private void OnSubagentMessage(int spawnId, string role, string content)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_subSessions.TryGetValue(spawnId, out var subName))
            {
                subName = _sessionManager.CreateChildSession(_sessionManager.CurrentSession, $"子任务 #{spawnId}");
                _subSessions[spawnId] = subName;
                _subStartTimes[spawnId] = Stopwatch.StartNew();
            }
            _sessionManager.LoadSession(subName);
            if (role == "user" || role == "User")
                _sessionManager.AddMessage(ChatRole.User, content);
            else
                _sessionManager.AddMessage(ChatRole.Assistant, content);
            _sessionManager.SaveSession(subName);
            _sessionManager.LoadSession(_sessionManager.CurrentSession);
        });
    }

    private void OnSubagentComplete(int spawnId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_subSessions.TryGetValue(spawnId, out var subName)) return;

            var elapsed = _subStartTimes.TryGetValue(spawnId, out var sw) ? sw.ElapsedMilliseconds : 0;
            var label = elapsed > 0
                ? $"子任务 #{spawnId} ({elapsed / 1000}.{(elapsed % 1000) / 100}s)"
                : $"子任务 #{spawnId}";
            _sessionManager.SaveMetadata(subName, new { ElapsedMs = elapsed, Label = label });
            AddSystemBubble($"✅ {label} 完成 — 在左侧会话列表中点击查看详情");
        });
    }

    private Action<LTAI.Agent.Tools.QuestionPost>? _questionHandler;

    // ── P17.5 Question Tool Integration ──

    private void SetupQuestionHandler()
    {
        var qs = App.Services?.GetService(typeof(LTAI.Agent.Tools.QuestionService))
            as LTAI.Agent.Tools.QuestionService;
        if (qs == null) return;

        _questionHandler = post =>
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = this.VisualRoot as Window;
                if (owner == null) return;
                var answers = await LTAI.Desktop.Dialogs.QuestionDialog.ShowAsync(owner, post);
                if (answers.Count > 0)
                    qs.Reply(post.RequestId, answers);
                else
                    qs.Reject(post.RequestId);
            });
        };
        qs.QuestionPosted += _questionHandler;
    }
}
