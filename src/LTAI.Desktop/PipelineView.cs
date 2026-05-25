using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class PipelineView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBlock _content;
    private readonly DispatcherTimer _timer;

    public PipelineView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };

        root.Children.Add(new TextBlock
        {
            Text = "Pipeline Dashboard",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });
        root.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border) });

        _content = new TextBlock
        {
            FontFamily = new("Consolas"),
            FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary)
        };
        root.Children.Add(_content);

        Content = new ScrollViewer { Content = root };

        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var p = System.Diagnostics.Process.GetCurrentProcess();
        var lts = _svc.LTS;

        _content.Text = string.Format(
            "LTAI Pipeline Status\n\n" +
            "Routing Layer\n" +
            "  Mode:        {0}\n" +
            "  Intent Router: Active\n" +
            "  Semantic Router: Active\n\n" +
            "Governors\n" +
            "  Input:       Active\n" +
            "  Context:     Active\n" +
            "  Routing:     Active\n" +
            "  Output:      Active\n" +
            "  Self:        Active\n\n" +
            "System\n" +
            "  PID:         {1}\n" +
            "  Threads:     {2}\n" +
            "  Memory:      {3} MB\n" +
            "  Uptime:      {4}\n" +
            "  .NET:        {5}\n\n" +
            "DNA\n" +
            "  State:       {6}\n" +
            "  Safety:      {7}",
            lts.Mode,
            p.Id,
            p.Threads.Count,
            p.WorkingSet64 / 1024 / 1024,
            DateTime.Now - p.StartTime,
            Environment.Version,
            _svc.DNA?.Consciousness.State.Level.ToString() ?? "Offline",
            _svc.DNA?.Safety.Posture.ToString() ?? "N/A");
    }
}
