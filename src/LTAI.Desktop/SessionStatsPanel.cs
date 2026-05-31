using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

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
        { Content = "➕", FontSize = 10, Width = 24, Height = 20,
          Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb("#ffffff") };
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
            if (_sessionList.SelectedItem is SessionInfo info)
                SessionSelected?.Invoke(info.Name);
        };
        _sessionList.ItemTemplate = new FuncDataTemplate<SessionInfo>((info, _) =>
        {
            var dock = new DockPanel { Margin = new(2, 1) };

            var delBtn = new Button
            {
                Content = "🗑",
                Width = 18, Height = 16,
                FontSize = 9,
                Background = LtaiTheme.Sbb(Colors.Transparent),
                BorderThickness = new(0),
                Padding = new(0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            delBtn.Click += async (_, _) =>
            {
                if (await ConfirmDeleteAsync(info.Name))
                {
                    _sessions.DeleteSession(info.Name);
                    Refresh();
                }
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            dock.Children.Add(delBtn);

            dock.Children.Add(new TextBlock
            {
                Text = info.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
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
        // 刷新会话列表
        var sessions = _sessions.ListSessions();
        _sessionList.ItemsSource = sessions;
        if (!string.IsNullOrEmpty(_sessions.CurrentSession))
            _sessionList.SelectedItem = sessions.FirstOrDefault(s => s.Name == _sessions.CurrentSession);

        // 刷新统计
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
