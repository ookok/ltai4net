using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using LTAI.Core.Session;

namespace LTAI.Desktop;

/// <summary>
/// 会话 + 统计合并面板。可折叠，不挤占聊天窗口。
/// </summary>
public sealed class SessionStatsPanel : UserControl
{
    private readonly StackPanel _root;
    private readonly StackPanel _sessionPanel;
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
        ToolTip.SetTip(toggleBtn, "会话列表与统计");
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
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), CornerRadius = LtaiTheme.Radius.Sm };
        newBtn.Click += (_, _) => NewSessionClicked?.Invoke();
        sessionHeader.Children.Add(newBtn);
        content.Children.Add(sessionHeader);

        var sessionScroller = new ScrollViewer
        {
            MaxHeight = 200,
            Content = _sessionPanel = new StackPanel { Spacing = 1 }
        };
        content.Children.Add(sessionScroller);

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

    private static string GetGroupKey(string sessionName)
    {
        // sessionName format: "session-YYYYMMdd-HHmmss" or "sub-..."
        var datePart = sessionName;
        var dashIdx = sessionName.LastIndexOf('-');
        if (dashIdx > 0 && sessionName.Length - dashIdx >= 9)
            datePart = sessionName.Substring(dashIdx + 1);
        if (datePart.Length >= 8 && int.TryParse(datePart.AsSpan(0, 8), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            var y = int.Parse(datePart.AsSpan(0, 4));
            var m = int.Parse(datePart.AsSpan(4, 2));
            var d = int.Parse(datePart.AsSpan(6, 2));
            var dt = new DateTime(y, m, d);
            var today = DateTime.Today;
            if (dt == today) return "今天";
            if (dt == today.AddDays(-1)) return "昨天";
            if (dt > today.AddDays(-(int)today.DayOfWeek - 6)) return "本周";
            if (dt.Year == today.Year && dt.Month == today.Month) return "本月";
            return "更早";
        }
        return "其他";
    }

    private static readonly string[] _groupOrder = { "今天", "昨天", "本周", "本月", "更早", "其他" };

    public void Refresh()
    {
        _sessionPanel.Children.Clear();
        var sessions = _sessions.ListSessions();
        var grouped = sessions
            .Where(s => s.ParentId == null)
            .GroupBy(s => GetGroupKey(s.Name))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Name).ToList());

        foreach (var gk in _groupOrder)
        {
            if (!grouped.TryGetValue(gk, out var group)) continue;
            _sessionPanel.Children.Add(new TextBlock
            {
                Text = $"── {gk} ({group.Count}) ──",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 10,
                FontFamily = LtaiTheme.CodeFont,
                Margin = new(4, 2, 0, 0)
            });
            foreach (var s in group)
            {
                var row = new DockPanel { Margin = new(4, 0, 0, 0) };
                var delBtn = new Button
                {
                    Content = "✕", FontSize = 10, Width = 20, Height = 18,
                    Background = LtaiTheme.Sbb(Colors.Transparent),
                    BorderThickness = new(0), Padding = new(0),
                    Tag = s.Name
                };
                delBtn.PointerEntered += (_, _) => delBtn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDanger, 64);
                delBtn.PointerExited += (_, _) => delBtn.Background = LtaiTheme.Sbb(Colors.Transparent);
                delBtn.Click += async (_, _) =>
                {
                    try
                    {
                        var name = (string)((Button)delBtn).Tag!;
                        if (await ConfirmDeleteAsync(name))
                        { _sessions.DeleteSession(name); Refresh(); }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Delete: {ex.Message}"); }
                };
                DockPanel.SetDock(delBtn, Dock.Right);
                row.Children.Add(delBtn);
                var label = new TextBlock
                {
                    Text = $"💬 {s.DisplayName}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    FontSize = 11,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                };
                label.PointerPressed += async (_, _) =>
                {
                    SessionSelected?.Invoke(s.Name);
                    await Task.CompletedTask;
                };
                row.Children.Add(label);
                _sessionPanel.Children.Add(row);

                // children
                var children = sessions.Where(c => c.ParentId == s.Name).OrderBy(c => c.Name).ToList();
                foreach (var c in children)
                {
                    var childLabel = c.DisplayName;
                    try
                    {
                        var metaDir = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "sessions");
                        var metaFile = Directory.GetFiles(metaDir, $"{c.Name}.meta.json").FirstOrDefault();
                        if (metaFile != null)
                        {
                            var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(File.ReadAllText(metaFile));
                            if (meta.TryGetProperty("ElapsedMs", out var el) && el.GetInt64() > 0)
                            {
                                var ms = el.GetInt64();
                                childLabel = $"[{ms / 1000}.{(ms % 1000) / 100}s] {c.DisplayName}";
                            }
                        }
                    }
                    catch { }
                    var childRow = new TextBlock
                    {
                        Text = $"  🔧 {childLabel}",
                        FontSize = 10,
                        Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                        Margin = new(4, 0, 0, 0)
                    };
                    _sessionPanel.Children.Add(childRow);
                }
            }
        }
        if (_sessionPanel.Children.Count == 0)
        {
            _sessionPanel.Children.Add(new TextBlock
            {
                Text = "  暂无会话",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 10,
                Margin = new(4, 0)
            });
        }

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
