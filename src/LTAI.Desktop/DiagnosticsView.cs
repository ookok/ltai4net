using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Core.Configuration;

namespace LTAI.Desktop;

public sealed class DiagnosticsView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBlock _content;
    private readonly DispatcherTimer _timer;

    public DiagnosticsView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };

        root.Children.Add(new TextBlock
        {
            Text = "LTAI V1.0 - Diagnostics",
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
            TimeSpan.FromSeconds(3),
            DispatcherPriority.Background,
            (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var dna = _svc.DNA;
        var p = System.Diagnostics.Process.GetCurrentProcess();
        var harness = ServiceLocator.Get<HarnessProfile>();

        _content.Text = string.Format(
            "LTAI V1.0 - Agent OS\n\n" +
            "Architecture:\n" +
            "  BaseAgent + IAnalysisStrategy pattern\n" +
            "  UnifiedSafetyGate with staircase penalty\n" +
            "  UnifiedSemanticRouter (embedding + keyword)\n" +
            "  UniversalOrchestrator (3-in-1 workflows)\n" +
            "  SentientParliament (3-way voting)\n" +
            "  ToolEvolutionLoop (observation mode)\n\n" +
            "Harness Layers (System Design):\n" +
            "  Task Decomposition → UniversalOrchestrator ({0})\n" +
            "  Tool Orchestration → LTAIToolRegistry\n" +
            "  Memory Storage    → KnowledgeGraph + CodeGraph\n" +
            "  Error Correction  → CorrectionMemory + GDN-2 Gate\n" +
            "  Audit & Delivery  → VerifiableRegistry + EvolutionStore\n\n" +
            "Profile: {1}  |  Safety: {2}  |  Evolution: {3}\n\n" +
            "DNA: {4}\n" +
            "Safety: {5}\n" +
            "Mode: {6}\n\n" +
            "System:\n" +
            "  PID: {7}  Threads: {8}\n" +
            "  MEM: {9} MB\n" +
            "  Uptime: {10:hh\\:mm\\:ss}\n" +
            "  .NET: {11}\n" +
            "  OS: {12}",
            harness.Mode,
            harness.Mode.ToString(),
            harness.SafetyPosture,
            harness.EnableEvolution ? harness.EvolutionAggressiveness : "off",
            dna != null ? string.Format("{0} (Gen {1})", dna.Consciousness.State.Level, dna.GetStatus().Generation) : "Offline",
            dna != null ? dna.Safety.Posture.ToString() : "Offline",
            _svc.LTS.Mode,
            p.Id,
            p.Threads.Count,
            p.WorkingSet64 / 1024 / 1024,
            DateTime.Now - p.StartTime,
            Environment.Version,
            Environment.OSVersion);
    }
}
