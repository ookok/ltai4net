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
        var cps = _svc.CPS;
        var scheduler = _svc.Scheduler;
        var router = _svc.Router;
        var kernel = _svc.Kernel;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("LTAI Pipeline Status\n");

        // CPS Stats
        if (cps != null)
        {
            try
            {
                var stats = cps.GetPerformanceStats();
                sb.AppendLine("CPS Performance");
                sb.AppendLine($"  Processed:    {stats.TotalProcessed}");
                sb.AppendLine($"  Avg Latency:  {stats.AvgLatencyMs}ms");
                sb.AppendLine($"  Est. Tokens:  {stats.EstimatedTotalTokens}");
                sb.AppendLine($"  Routes:       {string.Join(", ", stats.RouteDistribution.Select(kv => $"{kv.Key}:{kv.Value}"))}");
                sb.AppendLine();
            }
            catch { }
        }

        // Scheduler Health
        if (scheduler != null)
        {
            try
            {
                sb.AppendLine("System Health");
                sb.AppendLine($"  Scheduler:    {(scheduler.IsRunning ? "Running" : "Stopped")}");
                sb.AppendLine($"  Queue Depth:  {scheduler.QueueDepth}");
                sb.AppendLine($"  Events:       {scheduler.EventsProcessed}");
                sb.AppendLine($"  Rules Fired:  {scheduler.RulesTriggered}");
                sb.AppendLine();
            }
            catch { }
        }

        // ParetoRouter
        if (router != null)
        {
            try
            {
                sb.AppendLine("Pareto Router");
                sb.AppendLine($"  Frontier:     {router.FrontierSize} points");
                sb.AppendLine($"  Decisions:    {router.TotalDecisions}");
                sb.AppendLine($"  Shadow Rate:  {router.ShadowRate:P0}");
                sb.AppendLine();
            }
            catch { }
        }

        // Kernel Vitals
        if (kernel != null)
        {
            try
            {
                var vitals = kernel.GetAggregatedVitals();
                sb.AppendLine("MicroKernel");
                sb.AppendLine($"  Healthy:      {kernel.IsHealthy}");
                sb.AppendLine($"  P50:          {vitals.P50LatencyMs}ms");
                sb.AppendLine($"  P99:          {vitals.P99LatencyMs}ms");
                sb.AppendLine();
            }
            catch { }
        }

        // Routing + Governors
        sb.AppendLine("Routing Layer");
        sb.AppendLine($"  Mode:        {lts.Mode}");
        sb.AppendLine($"  Intent Router: Active");
        sb.AppendLine($"  Semantic Router: Active");
        sb.AppendLine();

        // System
        sb.AppendLine("System");
        sb.AppendLine($"  PID:         {p.Id}");
        sb.AppendLine($"  Threads:     {p.Threads.Count}");
        sb.AppendLine($"  Memory:      {p.WorkingSet64 / 1024 / 1024} MB");
        sb.AppendLine($"  Uptime:      {DateTime.Now - p.StartTime}");
        sb.AppendLine($"  .NET:        {Environment.Version}");
        sb.AppendLine();

        // DNA
        sb.AppendLine("DNA");
        sb.AppendLine($"  State:       {_svc.DNA?.Consciousness.State.Level.ToString() ?? "Offline"}");
        sb.AppendLine($"  Safety:      {_svc.DNA?.Safety.Posture.ToString() ?? "N/A"}");

        _content.Text = sb.ToString();
    }
}
