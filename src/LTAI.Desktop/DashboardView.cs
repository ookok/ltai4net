using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class DashboardView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBlock _sysText;
    private readonly TextBlock _healthText;
    private readonly TextBlock _sessionText;
    private readonly ProgressBar _contextBar;
    private readonly TextBlock _contextLabel;
    private readonly ProgressBar _cacheBar;
    private readonly TextBlock _cacheLabel;
    private readonly DispatcherTimer _timer;
    private static readonly Process _cachedProcess = Process.GetCurrentProcess();

    public DashboardView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };
        root.Children.Add(new TextBlock
        {
            Text = "LTAI 仪表盘",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });

        (_sysText, _) = AddPanel(root, "系统");
        (_healthText, _) = AddPanel(root, "运行时");
        (_sessionText, var sessionBorder) = AddPanel(root, "会话");

        // 上下文容量进度条 — 替换 Border 的 Child 为 StackPanel 以容纳文本+进度条
        _contextLabel = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
        };
        _contextBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 12,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Background = LtaiTheme.Sbb(Color.Parse("#1a2332")),
            Margin = new(0, 2, 0, 0),
        };
        var sessionContent = new StackPanel { Spacing = 2 };
        sessionContent.Children.Add(_sessionText);
        sessionContent.Children.Add(_contextLabel);
        sessionContent.Children.Add(_contextBar);
        // 缓存命中率进度条
        _cacheLabel = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
        };
        _cacheBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 12,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Background = LtaiTheme.Sbb(Color.Parse("#1a2332")),
            Margin = new(0, 2, 0, 0),
        };
        sessionContent.Children.Add(_cacheLabel);
        sessionContent.Children.Add(_cacheBar);

        Content = root;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        AttachedToVisualTree += (_, _) =>
        {
            Refresh();
            if (!_timer.IsEnabled) _timer.Start();
        };
        Refresh();
    }

    private void Refresh()
    {
        _cachedProcess.Refresh();
        _sysText.Text = $"模式: {_svc.Mode}\nDNA: {_svc.DNAStatus}\n安全: {_svc.SafetyPosture}\nPID: {_cachedProcess.Id}\n运行: {(_cachedProcess.StartTime != default ? DateTime.Now - _cachedProcess.StartTime : TimeSpan.Zero):hh\\:mm\\:ss}";
        _healthText.Text = $"GC 内存: {GC.GetTotalMemory(false) / 1024 / 1024} MB\n线程: {ThreadPool.ThreadCount}\n.NET: {Environment.Version}";
        _sessionText.Text = $"模型: {LTAI.Core.Configuration.UsageTracker.ActiveModel}\n"
                          + $"Token: {LTAI.Core.Configuration.UsageTracker.PromptTokens:N0}+{LTAI.Core.Configuration.UsageTracker.CompletionTokens:N0}={LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}\n"
                          + $"请求: {LTAI.Core.Configuration.UsageTracker.Requests}\n"
                          + $"费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}\n"
                          + $"运行: {LTAI.Core.Configuration.UsageTracker.Uptime:hh':'mm':'ss}\n"
                          + $"余额: {LTAI.Core.Configuration.UsageTracker.BalanceDisplay}";
        _contextBar.Value = LTAI.Core.Configuration.UsageTracker.ContextRatio(_svc.Options.AI.MaxTokens) * 100;
        _contextLabel.Text = $"上下文容量: {LTAI.Core.Configuration.UsageTracker.ContextText(_svc.Options.AI.MaxTokens)}";

        var totalCalls = LTAI.Core.Configuration.UsageTracker.CacheHits + LTAI.Core.Configuration.UsageTracker.CacheMisses;
        var hitRate = totalCalls > 0 ? LTAI.Core.Configuration.UsageTracker.CacheHitRate : 0;
        _cacheBar.Value = hitRate;
        _cacheLabel.Text = $"缓存命中: {hitRate:F1}% ({LTAI.Core.Configuration.UsageTracker.CacheHits}/{totalCalls})";
    }

    /// <summary>Add a panel to the dashboard. Returns (contentTextBlock, border) for further customization.</summary>
    private static (TextBlock, Border) AddPanel(StackPanel parent, string title)
    {
        var tb = new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        var contentTb = new TextBlock { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextWrapping = TextWrapping.Wrap };
        var border = new Border { BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border), BorderThickness = new(1), Padding = new(8), Child = contentTb };
        var panel = new StackPanel { Spacing = 6, Children = { tb, border } };
        parent.Children.Add(panel);
        return (contentTb, border);
    }
}
