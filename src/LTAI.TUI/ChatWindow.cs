using LTAI.Agent;
using LTAI.Core.Session;
using Microsoft.Extensions.AI;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using LTAI.TUI.Dialogs;

namespace LTAI.TUI;

/// <summary>
/// Main TUI — opencode-inspired layout.
///
/// Home (initial): FrameView → Logo → Prompt (actually an Editor input)
/// Chat (active):  Markdown messages + InputBar + Sidebar
///
/// The input bar lives on the home page. When the user submits their first
/// message, the home page hides and the chat page takes over.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly IApplication _app;
    private readonly ChatAgent _chat;
    private readonly SessionManager _sessionMgr;
    private CancellationTokenSource? _streamCts;
    private string _streamBuffer = "";
    private bool _chatStarted;

    private readonly FrameView _homePanel;
    private readonly View _chatPanel;
    private readonly Markdown _markdown;
    private readonly Editor _inputBar;       // shared: home prompt + chat input
    private readonly SpinnerView _spinner;
    private readonly FrameView _sidebar;
    private readonly List<string> _conv = new();
    private readonly Label _sidebarTokens;
    private readonly Label _sidebarStatus;
    private string _modelLabelText;
    private Label? _homeModelLabel;

    public MainWindow(IApplication app, ChatAgent chat, SessionManager sessionMgr, string l1ModelLabel = "未配置模型")
    {
        _app = app;
        _chat = chat;
        _sessionMgr = sessionMgr;
        Title = "LTAI";
        Width = Dim.Fill();
        Height = Dim.Fill();
        _modelLabelText = l1ModelLabel;

        // ═══════════════════════════════════
        //  HOME panel — opencode-style
        //  FrameView border → Logo → Editor prompt
        // ═══════════════════════════════════
        _homePanel = new FrameView
        {
            Id = "home", X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
            Title = " LTAI ",
        };

        _homePanel.Add(new Label
        {
            X = Pos.Center(), Y = 3,
            Text = "   ██╗     ████████╗ █████╗ ██╗\n" +
                   "   ██║     ╚══██╔══╝██╔══██╗██║\n" +
                   "   ██║        ██║   ███████║██║\n" +
                   "   ██║        ██║   ██╔══██║██║\n" +
                   "   ███████╗   ██║   ██║  ██║██║\n" +
                   "   ╚══════╝   ╚═╝   ╚═╝  ╚═╝╚═╝\n\n" +
                   "     LivingTree AI — 轻量版",
        });

        // Prompt input bar — Editor multiline, min 5 rows
        _inputBar = new Editor
        {
            X = Pos.Center(), Y = 14,
            Width = 50, Height = 5,
            Multiline = true,
        };
        _inputBar.KeyDown += OnInputKey;
        _inputBar.ContentChanged += OnContentChanged;
        _homePanel.Add(_inputBar);

        // Model info label below the input bar
        _homeModelLabel = new Label
        {
            X = Pos.Center(), Y = 21,
            Text = _modelLabelText,
        };
        _homePanel.Add(_homeModelLabel);

        // Shortcuts hint at the bottom
        var hintLabel = new Label
        {
            X = Pos.Center(), Y = 23,
            Text = "Ctrl+T 打开 TextPad  ·  Ctrl+Q 退出",
        };
        _homePanel.Add(hintLabel);

        // ═══════════════════════════════════
        //  CHAT panel — hidden until first message
        // ═══════════════════════════════════
        _chatPanel = new View
        {
            Id = "chat", Visible = false,
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
        };

        // Sidebar
        _sidebarTokens = new Label { X = 0, Y = 1, Text = "消息: 0" };
        _sidebarStatus = new Label { X = 0, Y = 2, Text = "状态: 就绪" };
        _sidebar = new FrameView
        {
            X = Pos.AnchorEnd(24), Y = 0,
            Width = 24, Height = Dim.Fill() - 2,
            Title = "统计",
        };
        _sidebar.Add(new Label { X = 0, Y = 0, Text = "LTAI" }, _sidebarTokens, _sidebarStatus);

        // Messages
        _markdown = new Markdown
        {
            X = 0, Y = 0,
            Width = Dim.Fill() - 24,
            Height = Dim.Fill() - 2,
            CanFocus = true,
            ShowCopyButtons = true,
            SyntaxHighlighter = new TextMateSyntaxHighlighter(TextMateSharp.Grammars.ThemeName.DarkPlus),
        };

        // Spinner
        _spinner = new SpinnerView
        {
            Style = new SpinnerStyle.Dots(),
            X = Pos.Center(), Y = Pos.Bottom(_markdown),
            Width = 4, Visible = false,
        };

        _chatPanel.Add(_markdown, _sidebar, _spinner);

        Add(_homePanel, _chatPanel);

        // Focus the input bar immediately
        _inputBar.SetFocus();

        RestoreSession();
    }

    // ── Command picker ──
    private static readonly (string cmd, string desc)[] _commands = new[]
    {
        ("model",    "配置/查看模型"),
        ("new",      "新建会话"),
        ("clear",    "清空对话"),
        ("retry",    "重试上一条"),
        ("status",   "当前状态"),
        ("theme",    "切换 Dark/Light 主题"),
        ("commands", "全部命令列表"),
        ("help",     "帮助"),
        ("exit",     "退出应用"),
    };
    private Dialog? _commandPicker;

    private void ShowCommandPicker()
    {
        _commandPicker?.Dispose();
        _commandPicker = new Dialog
        {
            Title = "命令选择器",
            Width = 36, Height = 10,
            X = Pos.Center(), Y = Pos.Center(),
        };

        var items = _commands.Select(c => $"/{c.cmd,-8} {c.desc}").ToList();
        var list = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
        };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(items));

        // OpenSelectedItem → dialog Accept fired via list KeyDown
        list.KeyDown += (s, k) =>
        {
            if (k == Key.Enter)
            {
                var idx = list.SelectedItem ?? 0;
                if (idx >= 0 && idx < _commands.Length)
                {
                    var cmd = _commands[idx].cmd;
                    _commandPicker.Dispose(); _commandPicker = null;
                    _inputBar.SetFocus();
                    ExecuteCommand(cmd);
                }
                else
                {
                    _commandPicker.Dispose(); _commandPicker = null;
                    _inputBar.SetFocus();
                }
                k.Handled = true;
            }
            if (k == Key.Esc)
            {
                _commandPicker.Dispose(); _commandPicker = null;
                _inputBar.SetFocus();
                k.Handled = true;
            }
        };

        _commandPicker.Add(list);
        var cancelBtn = new Button { Text = "_取消" };
        cancelBtn.Accepting += (_, _) => { _commandPicker.Dispose(); _commandPicker = null; _inputBar.SetFocus(); };
        _commandPicker.AddButton(cancelBtn);
        _commandPicker.Visible = true;
        Add(_commandPicker);
        list.SetFocus();
    }

    private void OnContentChanged(object? s, DocumentChangeEventArgs e)
    {
        if (_inputBar.Text == "/" && _commandPicker == null)
            ShowCommandPicker();
        if (_commandPicker != null && _inputBar.Text.Length == 0)
        { _commandPicker?.Dispose(); _commandPicker = null; }
    }

    private void OnInputKey(object? s, Key k)
    {
        // Esc dismisses command picker
        if (k == Key.Esc && _commandPicker != null)
        {
            _commandPicker?.Dispose(); _commandPicker = null;
            _inputBar.SetFocus();
            k.Handled = true;
            return;
        }

        // Backspace: manual delete (Editor's native Backspace has issues on this build)
        if (k == Key.Backspace)
        {
            var pos = _inputBar.CaretOffset;
            var txt = _inputBar.Text;
            if (pos > 0 && txt.Length > 0)
            {
                _inputBar.Text = txt.Remove(pos - 1, 1);
                _inputBar.CaretOffset = Math.Max(0, pos - 1);
            }
            k.Handled = true;
            return;
        }

        // Shift+Enter: insert newline in multiline mode
        if (k == Key.Enter && k.IsShift)
        {
            var pos = _inputBar.CaretOffset;
            var txt = _inputBar.Text;
            _inputBar.Text = txt.Insert(pos, "\n");
            _inputBar.CaretOffset = pos + 1;
            k.Handled = true;
            return;
        }

        // Plain Enter: submit
        if (k != Key.Enter) return;
        k.Handled = true;
        var text = _inputBar.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // ── Handle command BEFORE transition (Editor is stable in home panel) ──
        if (text.StartsWith("/"))
        {
            var p = text.TrimStart('/').Split(' ');
            _inputBar.Text = "";
            ExecuteCommand(p[0].ToLowerInvariant());
            return;
        }

        // ── Transition home→chat (after command handling, only for non-command text) ──
        if (!_chatStarted)
        {
            _chatStarted = true;
            _homePanel.Visible = false;
            _chatPanel.Visible = true;

            _homePanel.Remove(_inputBar);
            _inputBar.Y = Pos.Bottom(_markdown);
            _inputBar.Width = Dim.Fill();
            _inputBar.X = 0;
            _inputBar.Height = 3;
            _chatPanel.Add(_inputBar);
            var ml = new Label { X = 0, Y = Pos.Bottom(_inputBar), Text = _modelLabelText };
            _chatPanel.Add(ml);
            _inputBar.SetFocus();
        }

        _inputBar.Text = "";

        if (text.StartsWith("/"))
        {
            var p = text.TrimStart('/').Split(' ');
            switch (p[0].ToLowerInvariant())
            {
                case "new": _conv.Clear(); _markdown.Text = ""; break;
                case "model": HandleModelCommand(); break;
                case "retry":  AddMsg("System", "重发暂未实现"); break;
                case "clear": _conv.Clear(); _markdown.Text = ""; break;
                case "status":
                    AddMsg("System", $"**状态**\n- 消息数: {_conv.Count}\n- 模型: {_modelLabelText}\n- 会话: {_sessionMgr.CurrentHandle?.Name ?? "—"}");
                    break;
                case "commands":
                    AddMsg("System", "**可用命令**\n\n" +
                        "`/model`  配置/查看模型\n" +
                        "`/new`    新建会话\n" +
                        "`/clear`  清空对话\n" +
                        "`/retry`  重试上一条\n" +
                        "`/status` 当前状态\n" +
                        "`/help`   显示帮助\n" +
                        "`/exit`   退出应用");
                    break;
                case "help":
                    AddMsg("System", "输入 `/commands` 查看全部命令\n快捷键: `Ctrl+Q` 退出");
                    break;
                case "exit": _app.RequestStop(); break;
                default: AddMsg("System", $"未知 `/{p[0]}`"); break;
            }
            return;
        }

        AddMsg("You", text);
        _streamCts = new CancellationTokenSource();
        _ = StreamAsync(text, _streamCts.Token);
    }

    private void AddMsg(string role, string md)
    {
        _conv.Add($"**{role}:** {md}");
        _markdown.Text = string.Join("\n\n---\n\n", _conv);
        _sidebarTokens.Text = $"消息: {_conv.Count}";
    }

    private async Task StreamAsync(string input, CancellationToken ct)
    {
        var handle = _sessionMgr.CurrentHandle;
        _streamBuffer = "";
        _app.Invoke(() => { _spinner.Visible = true; _spinner.AutoSpin = true; _sidebarStatus.Text = "状态: 思考中..."; });
        _conv.Add("**AI:** ");
        _markdown.Text = string.Join("\n\n---\n\n", _conv);
        try
        {
            await foreach (var u in _chat.ChatStreamingAsync(input, handle).WithCancellation(ct))
            {
                if (ct.IsCancellationRequested) break;
                var t = u.Text ?? ""; if (t.Length == 0) continue;
                _streamBuffer += t;
                _app.Invoke(() => { if (_conv.Count > 0) { _conv[^1] = $"**AI:** {_streamBuffer}"; _markdown.Text = string.Join("\n\n---\n\n", _conv); } });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _app.Invoke(() => AddMsg("System", $"⚠ {ex.Message}")); }
        finally
        {
            _app.Invoke(() => { _spinner.Visible = false; _spinner.AutoSpin = false; _sidebarStatus.Text = "状态: 就绪"; });
            if (!ct.IsCancellationRequested) await _sessionMgr.SaveSessionAsync();
        }
    }

    private void ExecuteCommand(string cmd)
    {
        switch (cmd)
        {
            case "new":   _conv.Clear(); _markdown.Text = ""; return;
            case "clear": _conv.Clear(); _markdown.Text = ""; return;
            case "retry": AddMsg("System", "重发暂未实现"); return;
            case "model": HandleModelCommand(); return;
            case "status":
                AddMsg("System", $"**状态**\n- 消息数: {_conv.Count}\n- 模型: {_modelLabelText}\n- 会话: {_sessionMgr.CurrentHandle?.Name ?? "—"}");
                return;
            case "commands":
                AddMsg("System", "**可用命令**\n\n`/model` 配置模型 `/new` 新建 `/clear` 清空 `/theme` 切换主题 `/retry` 重试 `/status` 状态 `/help` 帮助 `/exit` 退出");
                return;
            case "theme":
                var themeNames = Terminal.Gui.Configuration.ThemeManager.GetThemeNames().ToList();
                var curTheme = Terminal.Gui.Configuration.ThemeManager.Theme;
                var nextTheme = themeNames.FirstOrDefault(n => n != curTheme) ?? curTheme;
                Terminal.Gui.Configuration.ThemeManager.Theme = nextTheme;
                Terminal.Gui.Configuration.ConfigurationManager.Apply();
                AddMsg("System", $"主题: {curTheme} → {nextTheme}");
                return;
            case "help":   AddMsg("System", "输入 `/commands` 查看全部命令"); return;
            case "exit":   _app.RequestStop(); return;
            default:       AddMsg("System", $"未知 `/{cmd}`"); return;
        }
    }

    private void HandleModelCommand()
    {
        try
        {
            ModelConfigDialog.Run(_app, "L1");
            UpdateModelLabel();
            _app.LayoutAndDraw(true);
        }
        catch (Exception ex) { AddMsg("System", $"⚠ 模型配置错误: {ex.Message}"); }
    }

    private void UpdateModelLabel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (!File.Exists(path)) return;
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            var l1 = json?["LTAI"]?["AI"]?["L1"];
            var provider = l1?["Provider"]?.GetValue<string>() ?? "";
            var model = l1?["Model"]?.GetValue<string>() ?? "";
            _modelLabelText = !string.IsNullOrEmpty(provider) ? $"L1: {provider} / {model}" : "未配置模型 (使用 /model 配置)";
            if (_homeModelLabel != null) _homeModelLabel.Text = _modelLabelText;
        }
        catch { }
    }

    private void RestoreSession()
    {
        try
        {
            var sessions = _sessionMgr.ListSessions();
            if (sessions.Length == 0) return;
            var h = _sessionMgr.LoadSession(sessions[^1].Name);
            if (h?.Messages is { Count: > 0 } msgs)
            {
                _chatStarted = true;
                _homePanel.Visible = false;
                _chatPanel.Visible = true;
                _homePanel.Remove(_inputBar);
                _inputBar.Y = Pos.Bottom(_markdown);
                _inputBar.Width = Dim.Fill();
                _inputBar.X = 0;
                _chatPanel.Add(_inputBar);
                _inputBar.SetFocus();
                foreach (var m in msgs)
                    _conv.Add($"**{(m.Role == ChatRole.User ? "You" : "AI")}:** {m.Text ?? ""}");
                _markdown.Text = string.Join("\n\n---\n\n", _conv);
                _sidebarTokens.Text = $"消息: {_conv.Count}";
            }
        }
        catch { }
    }
}
