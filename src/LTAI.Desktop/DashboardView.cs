using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class DashboardView : UserControl
{
    private readonly LTAIService _svc;
    private readonly StackPanel _dnaPanel;
    private readonly StackPanel _sysPanel;
    private readonly StackPanel _healthPanel;
    private readonly StackPanel _sessionPanel;
    private readonly TextBlock _dnaText;
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
            Text = "LTAI V0.51 - Sentient Mesh",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });
        root.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border) });

        var grid = new Grid
        {
            ColumnDefinitions = new("*,*"),
            RowDefinitions = new("Auto,Auto")
        };

        _dnaPanel = Panel("DNA Status", LtaiTheme.AccentDNA);
        _sysPanel = Panel("System", LtaiTheme.AccentSystem);
        _healthPanel = Panel("Health", LtaiTheme.AccentWarning);
        _sessionPanel = Panel("Session", LtaiTheme.AccentInfo);

        Grid.SetColumn(_dnaPanel, 0); Grid.SetRow(_dnaPanel, 0);
        Grid.SetColumn(_sysPanel, 1); Grid.SetRow(_sysPanel, 0);
        Grid.SetColumn(_healthPanel, 0); Grid.SetRow(_healthPanel, 1);
        Grid.SetColumn(_sessionPanel, 1); Grid.SetRow(_sessionPanel, 1);

        grid.Children.Add(_dnaPanel);
        grid.Children.Add(_sysPanel);
        grid.Children.Add(_healthPanel);
        grid.Children.Add(_sessionPanel);

        _dnaText = CreateText();
        _sysText = CreateText();
        _healthText = CreateText();
        _sessionText = CreateText();

        _dnaPanel.Children.Add(_dnaText);
        _sysPanel.Children.Add(_sysText);
        _healthPanel.Children.Add(_healthText);
        _sessionPanel.Children.Add(_sessionText);

        root.Children.Add(grid);

        root.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border) });

        var shortcuts = CreateText();
        shortcuts.Text = "Ctrl+T Theme | Ctrl+1-6 Tabs | Esc Dashboard | Enter Chat";
        shortcuts.Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim);
        shortcuts.FontSize = 11;
        root.Children.Add(shortcuts);

        Content = new ScrollViewer { Content = root };

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var dna = _svc.DNA;
        var p = System.Diagnostics.Process.GetCurrentProcess();
        var uptime = DateTime.Now - p.StartTime;

        _dnaText.Text = dna != null
            ? string.Format("Consciousness: {0}\nAwareness: {1:F2}\nSafety: {2}\nGeneration: {3}\nFitness: {4:F2}",
                dna.Consciousness.State.Level,
                dna.Consciousness.State.AwarenessScore,
                dna.Safety.Posture,
                dna.GetStatus().Generation,
                dna.GetStatus().FitnessScore)
            : "DNA: Offline";

        _sysText.Text = string.Format("Mode: {0}\nPID: {1}\nThreads: {2}\nMEM: {3} MB\nUptime: {4:hh\\:mm\\:ss}\n.NET: {5}",
            _svc.LTS.Mode, p.Id, p.Threads.Count,
            p.WorkingSet64 / 1024 / 1024, uptime, Environment.Version);

        _healthText.Text = string.Format("GC Memory: {0} MB\nHeap: {1} MB\nThreads: {2}\nRuntime: {3}",
            GC.GetTotalMemory(false) / 1024 / 1024,
            GC.GetGCMemoryInfo().HeapSizeBytes / 1024 / 1024,
            ThreadPool.ThreadCount,
            Environment.Version);

        _sessionText.Text = string.Format("Mode: {0}\nSafety: {1}\nPID: {2}\nMEM: {3} MB",
            _svc.LTS.Mode,
            dna?.Safety.Posture.ToString() ?? "N/A",
            p.Id,
            p.WorkingSet64 / 1024 / 1024);

        var metrics = _svc.Metrics;
        if (metrics != null)
        {
            var s = metrics.GetSnapshot();
            _sessionText.Text += string.Format("\nRequests: {0}\nTokens: {1:n0}", s.TotalRequests, s.TotalTokens);
        }
    }

    private static StackPanel Panel(string header, Color accent)
    {
        var sp = new StackPanel { Spacing = 6 };
        var b = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(accent),
            BorderThickness = new(1),
            CornerRadius = new(6),
            Padding = new(10)
        };
        sp.Children.Add(new TextBlock { Text = header, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = LtaiTheme.Sbb(accent) });
        sp.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border) });
        b.Child = sp;
        var outer = new StackPanel();
        outer.Children.Add(b);
        return outer;
    }

    private static TextBlock CreateText() => new()
    {
        Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
        FontSize = 13,
        FontFamily = new("Consolas"),
        TextWrapping = TextWrapping.Wrap
    };
}
