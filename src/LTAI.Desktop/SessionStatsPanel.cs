using System.Linq;
using Avalonia.Controls;
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
        _sessionList.SelectionChanged += (_, e) =>
        {
            if (_sessionList.SelectedItem is string name)
                SessionSelected?.Invoke(name);
        };
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
            _sessionList.SelectedItem = sessions.FirstOrDefault(s => s == _sessions.CurrentSession);

        // 刷新统计
        _statsText.Text = $"模型: {LTAI.Core.Configuration.UsageTracker.ActiveModel}\n"
                        + $"Token: {LTAI.Core.Configuration.UsageTracker.PromptTokens:N0}+{LTAI.Core.Configuration.UsageTracker.CompletionTokens:N0}={LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}\n"
                        + $"请求: {LTAI.Core.Configuration.UsageTracker.Requests}  缓存: {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%\n"
                        + $"费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}  运行: {LTAI.Core.Configuration.UsageTracker.Uptime:hh':'mm':'ss}\n"
                        + $"余额: {LTAI.Core.Configuration.UsageTracker.BalanceDisplay}";
    }

    private void UpdateVisibility()
    {
        if (_root.Children.Count > 1 && _root.Children[1] is StackPanel sp)
            sp.IsVisible = _expanded;
    }
}
