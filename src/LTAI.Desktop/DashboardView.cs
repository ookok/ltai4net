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
    private readonly DispatcherTimer _timer;

    public DashboardView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };
        root.Children.Add(new TextBlock
        {
            Text = "LTAI Dashboard",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });

        (_sysText, _) = AddPanel(root, "System");
        (_healthText, _) = AddPanel(root, "Runtime");
        (_sessionText, _) = AddPanel(root, "Session");

        Content = root;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var p = System.Diagnostics.Process.GetCurrentProcess();
        var uptime = DateTime.Now - p.StartTime;
        _sysText.Text = $"Mode: {_svc.Mode}\nDNA: {_svc.DNAStatus}\nSafety: {_svc.SafetyPosture}\nPID: {p.Id}\nUptime: {uptime:hh\\:mm\\:ss}";
        _healthText.Text = $"GC Memory: {GC.GetTotalMemory(false) / 1024 / 1024} MB\nThreads: {ThreadPool.ThreadCount}\n.NET: {Environment.Version}";
        _sessionText.Text = $"Tokens: {_svc.TokensUsed:N0}\nRequests: {_svc.RequestsThisSession}\nAvg Latency: {_svc.AvgLatencyMs:F1}ms";
    }

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
