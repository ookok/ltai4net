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
    private readonly TextBlock _dnaText;
    private readonly TextBlock _sysText;
    private readonly DispatcherTimer _timer;

    public DashboardView(LTAIService svc)
    {
        _svc = svc;
        Background = new SolidColorBrush(Color.Parse("#0d1117"));

        var root = new StackPanel { Spacing = 12, Margin = new(16) };

        var title = new TextBlock { Text = "LTAI v7.0 — Sentient Mesh", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#f0f6fc")) };
        root.Children.Add(title);
        root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#30363d")) });

        var grid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto") };
        _dnaPanel = Panel("DNA Status", "#58a6ff");
        _sysPanel = Panel("System", "#3fb950");
        Grid.SetColumn(_dnaPanel, 0); Grid.SetColumn(_sysPanel, 1);
        grid.Children.Add(_dnaPanel); grid.Children.Add(_sysPanel);
        root.Children.Add(grid);

        _dnaText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#8b949e")), FontSize = 13, FontFamily = new("Consolas") };
        _sysText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#8b949e")), FontSize = 13, FontFamily = new("Consolas") };
        _dnaPanel.Children.Add(_dnaText);
        _sysPanel.Children.Add(_sysText);

        Content = new ScrollViewer { Content = root };

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var dna = _svc.DNA;
        var p = System.Diagnostics.Process.GetCurrentProcess();

        _dnaText.Text = dna != null
            ? $"Consciousness: {dna.Consciousness.State.Level}\nAwareness: {dna.Consciousness.State.AwarenessScore:F2}\nSafety: {dna.Safety.Posture}\nGeneration: {dna.GetStatus().Generation}\nFitness: {dna.GetStatus().FitnessScore:F2}"
            : "DNA: Offline";

        _sysText.Text = $"Mode: {_svc.LTS.Mode}\nPID: {p.Id}  Threads: {p.Threads.Count}\nMEM: {p.WorkingSet64 / 1024 / 1024}MB\nUptime: {DateTime.Now - p.StartTime:hh\\:mm\\:ss}\n.NET: {Environment.Version}";

        var metrics = _svc.Metrics;
        if (metrics != null)
        {
            var s = metrics.GetSnapshot();
            _sysText.Text += $"\nRequests: {s.TotalRequests}  Tokens: {s.TotalTokens:n0}";
        }
    }

    private static StackPanel Panel(string header, string accent)
    {
        var sp = new StackPanel { Spacing = 6, Margin = new(4) };
        sp.Children.Add(new TextBlock { Text = header, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(accent)) });
        sp.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#30363d")) });
        return sp;
    }
}
