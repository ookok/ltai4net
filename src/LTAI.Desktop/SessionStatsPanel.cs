using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

public sealed record SessionTreeItem(SessionInfo Info, int Depth);

/// <summary>
/// 会话 + 统计合并面板。可折叠，不挤占聊天窗口。
/// </summary>
public sealed class SessionStatsPanel : UserControl
{
    private readonly StackPanel _root;
    private readonly ListBox _sessionList;
    private readonly TextBlock _statsText;
    private readonly SessionManager _sessions;
    private bool _expanded;
    private bool _suppressSelection;

    /// <summary>展开/折叠切换事件。</summary>
    public event Action<string?>? SessionSelected;
    public event Action? NewSessionClicked;

    public bool IsExpanded => _expanded;

    public SessionStatsPanel(SessionManager sessions)
    {
        _sessions = sessions;
        _root = new StackPanel { Spacing = 4 };

        // 折叠按钮
        var toggleBtn = new Button
        {
            Content = "📋 会话与统计",
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            Height = 24,
        };
        toggleBtn.Click += (_, _) => { _expanded = !_expanded; UpdateVisibility(); };
        _root.Children.Add(toggleBtn);

        // 折叠内容区域
        var content = new StackPanel { Spacing = 4, IsVisible = false };

        // ── 会话管理 ──
        var sessionHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(4, 0, 0, 0),
        };
        sessionHeader.Children.Add(new TextBlock
        { Text = "会话", FontWeight = FontWeight.Bold, FontSize = 11,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), VerticalAlignment = VerticalAlignment.Center });

        var newBtn = new Button
        { Content = "  ➕  新建", FontSize = 11, Width = 60, Height = 24,
          Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb("#ffffff"), CornerRadius = new(4) };
        newBtn.Click += (_, _) => NewSessionClicked?.Invoke();
        sessionHeader.Children.Add(newBtn);
        content.Children.Add(sessionHeader);

        _sessionList = new ListBox
        {
            MinHeight = 80, MaxHeight = 200,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontSize = 11,
        };
        _sessionList.SelectionChanged += (_, _) =>
        {
            if (_suppressSelection) return;
            if (_sessionList.SelectedItem is SessionTreeItem item)
                SessionSelected?.Invoke(item.Info.Name);
        };
        _sessionList.ItemTemplate = new FuncDataTemplate<SessionTreeItem>((item, _) =>
        {
            var dock = new DockPanel { Margin = new(2 + item.Depth * 12, 1) };

            var delBtn = new Button
            {
                Content = "✕",
                FontSize = 10,
                Width = 22, Height = 20,
                Background = LtaiTheme.Sbb(Colors.Transparent),
                BorderThickness = new(0),
                Padding = new(0),
            };
            delBtn.PointerEntered += (_, _) => delBtn.Background = LtaiTheme.Sbb(Color.Parse("#f8514940"));
            delBtn.PointerExited += (_, _) => delBtn.Background = LtaiTheme.Sbb(Colors.Transparent);
            delBtn.Click += async (_, _) =>
            {
                try
                {
                    if (await ConfirmDeleteAsync(item.Info.Name))
                    {
                        _sessions.DeleteSession(item.Info.Name);
                        Refresh();
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Delete session: {ex.Message}"); }
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            dock.Children.Add(delBtn);

            var icon = item.Depth == 0 ? "💬" : "🔧";
            dock.Children.Add(new TextBlock
            {
                Text = $"{icon} {item.Info.DisplayName}",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = LtaiTheme.Sbb(item.Depth == 0 ? LtaiTheme.TextPrimary : LtaiTheme.TextSecondary),
                FontSize = item.Depth == 0 ? 11 : 10
            });

            return dock;
        });
        content.Children.Add(_sessionList);

        // ── 统计信息 ──
        content.Children.Add(new TextBlock
        { Text = "会话统计", FontWeight = FontWeight.Bold, FontSize = 11, Margin = new(4, 4, 0, 0),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _statsText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 10, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 150,
        };
        content.Children.Add(_statsText);

        _root.Children.Add(content);
        Content = _root;
        Refresh();
    }

    public void Refresh()
    {
        var sessions = _sessions.ListSessions();
        var flatList = new List<SessionTreeItem>();
        foreach (var s in sessions)
        {
            if (s.ParentId != null) continue;
            flatList.Add(new SessionTreeItem(s, 0));
            var children = sessions.Where(c => c.ParentId == s.Name).OrderBy(c => c.Name);
            foreach (var c in children)
            {
                // 读取子会话元数据中的耗时
                var label = c.DisplayName;
                try
                {
                    var metaPath = Path.Combine(
                        Path.GetDirectoryName(Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "sessions"), $"{c.Name}.meta.json").FirstOrDefault() ?? ""),
                        $"{c.Name}.meta.json");
                    if (File.Exists(metaPath))
                    {
                        var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(File.ReadAllText(metaPath));
                        if (meta.TryGetProperty("ElapsedMs", out var el) && el.GetInt64() > 0)
                        {
                            var ms = el.GetInt64();
                            var timeStr = $"{ms / 1000}.{(ms % 1000) / 100}s";
                            label = $"[{timeStr}] {c.DisplayName}";
                        }
                    }
                }
                catch { }
                flatList.Add(new SessionTreeItem(new SessionInfo(c.Name, label, c.ParentId), 1));
            }
        }
        _suppressSelection = true;
        _sessionList.ItemsSource = flatList;
        if (!string.IsNullOrEmpty(_sessions.CurrentSession))
        {
            var current = flatList.FirstOrDefault(f => f.Info.Name == _sessions.CurrentSession);
            if (current != null)
                _sessionList.SelectedItem = current;
        }
        _suppressSelection = false;

        _statsText.Text = $"模型: {LTAI.Core.Configuration.UsageTracker.ActiveModel}\n"
                        + $"Token: {LTAI.Core.Configuration.UsageTracker.PromptTokens:N0}+{LTAI.Core.Configuration.UsageTracker.CompletionTokens:N0}={LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}\n"
                        + $"请求: {LTAI.Core.Configuration.UsageTracker.Requests}  缓存: {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%\n"
                        + $"费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}  运行: {LTAI.Core.Configuration.UsageTracker.Uptime:hh':'mm':'ss}\n"
                        + $"余额: {LTAI.Core.Configuration.UsageTracker.BalanceDisplay}";
    }

    private async Task<bool> ConfirmDeleteAsync(string name)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window owner) return false;

        var dialog = new Window
        {
            Title = "删除会话",
            Width = 320,
            Height = 130,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var panel = new StackPanel { Spacing = 10, Margin = new(15) };
        panel.Children.Add(new TextBlock
        {
            Text = $"确认删除会话 \"{name}\" ？",
            TextWrapping = TextWrapping.Wrap,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 10, 0, 0)
        };
        var yesBtn = new Button { Content = "删除", Width = 60 };
        var noBtn = new Button { Content = "取消", Width = 60 };
        btnPanel.Children.Add(yesBtn);
        btnPanel.Children.Add(noBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        var result = false;
        yesBtn.Click += (_, _) => { result = true; dialog.Close(); };
        noBtn.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return result;
    }

    private void UpdateVisibility()
    {
        if (_root.Children.Count > 1 && _root.Children[1] is StackPanel sp)
            sp.IsVisible = _expanded;
    }
}
