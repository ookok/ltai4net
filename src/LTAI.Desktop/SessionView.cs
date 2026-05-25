using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class SessionView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBlock _content;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _startTime = DateTime.Now;

    public SessionView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };

        root.Children.Add(new TextBlock
        {
            Text = "Session Information",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });
        root.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border) });

        _content = new TextBlock
        {
            FontFamily = new("Consolas"),
            FontSize = 14,
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
        var uptime = DateTime.Now - _startTime;

        _content.Text = string.Format(
            "Session Status\n\n" +
            "Session ID:    {0:X8}\n" +
            "Start Time:    {1:yyyy-MM-dd HH:mm:ss}\n" +
            "Uptime:        {2:hh\\:mm\\:ss}\n\n" +
            "System Resources\n" +
            "PID:           {3}\n" +
            "Threads:       {4}\n" +
            "Working Set:   {5} MB\n" +
            "OS Version:    {6}\n" +
            ".NET Version:  {7}\n\n" +
            "DNA Status\n" +
            "Consciousness: {8}\n" +
            "Awareness:     {9:F2}\n" +
            "Generation:    {10}\n" +
            "Fitness:       {11:F2}\n" +
            "Safety:        {12}\n\n" +
            "Operating Mode: {13}",
            GetHashCode() & 0xFFFFFFFF,
            _startTime,
            uptime,
            p.Id,
            p.Threads.Count,
            p.WorkingSet64 / 1024 / 1024,
            Environment.OSVersion,
            Environment.Version,
            _svc.DNA?.Consciousness.State.Level.ToString() ?? "Offline",
            _svc.DNA?.Consciousness.State.AwarenessScore ?? 0,
            _svc.DNA?.GetStatus().Generation ?? 0,
            _svc.DNA?.GetStatus().FitnessScore ?? 0,
            _svc.DNA?.Safety.Posture.ToString() ?? "N/A",
            _svc.LTS.Mode);
    }
}
