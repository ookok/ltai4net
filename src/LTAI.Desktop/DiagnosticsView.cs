using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class DiagnosticsView : UserControl
{
    private readonly TextBlock _content;
    private readonly DispatcherTimer _timer;

    public DiagnosticsView(LTAIService svc)
    {
        Background = new SolidColorBrush(Color.Parse("#0d1117"));
        var root = new StackPanel { Spacing = 12, Margin = new(16) };

        root.Children.Add(new TextBlock { Text = "LTAI v7.0 — Diagnostics", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#f0f6fc")) });
        root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#30363d")) });

        _content = new TextBlock { FontFamily = new("Consolas"), FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#8b949e")) };
        root.Children.Add(_content);

        Content = new ScrollViewer { Content = root };

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();

        Refresh(svc);
    }

    private void Refresh() => Refresh(ServiceLocator.Get<LTAIService>());

    private void Refresh(LTAIService svc)
    {
        var dna = svc.DNA;
        var p = System.Diagnostics.Process.GetCurrentProcess();

        _content.Text = $"""
            LTAI v7.0.0 — Sentient Mesh

            Architecture:
              ✅ BaseAgent + IAnalysisStrategy pattern
              ✅ UnifiedSafetyGate with staircase penalty
              ✅ UnifiedSemanticRouter (embedding + keyword)
              ✅ UniversalOrchestrator (3-in-1 workflows)
              ✅ RegulationVersionStore with integrity checks
              ✅ SentientParliament (3-way voting)
              ✅ ToolEvolutionLoop (observation mode)

            DNA: {(dna != null ? $"{dna.Consciousness.State.Level} (Gen {dna.GetStatus().Generation})" : "Offline")}
            Safety: {(dna != null ? dna.Safety.Posture.ToString() : "Offline")}
            Mode: {svc.LTS.Mode}

            System:
              PID: {p.Id}  Threads: {p.Threads.Count}
              MEM: {p.WorkingSet64 / 1024 / 1024} MB
              Uptime: {DateTime.Now - p.StartTime:hh\\:mm\\:ss}
              .NET: {Environment.Version}
              OS: {Environment.OSVersion}
            """;
    }
}
