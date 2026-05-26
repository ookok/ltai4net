using System.Diagnostics;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class HealthDashboard
{
    private readonly Process _process = Process.GetCurrentProcess();
    private long _lastTotalProcessorTime;
    private DateTime _lastCpuTime;
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _memHistory = new();

    public HealthDashboard()
    {
        _lastTotalProcessorTime = _process.TotalProcessorTime.Ticks;
        _lastCpuTime = DateTime.UtcNow;
    }

    public IRenderable Render()
    {
        var cpu = GetCpuUsage();
        var mem = _process.WorkingSet64 / 1024.0 / 1024.0;
        var gc = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
        var threads = _process.Threads.Count;
        var uptime = DateTime.UtcNow - _process.StartTime.ToUniversalTime();
        var handleCount = 0;
        try { handleCount = _process.HandleCount; } catch { }

        _cpuHistory.Enqueue(cpu);
        _memHistory.Enqueue(mem);
        while (_cpuHistory.Count > 30) _cpuHistory.Dequeue();
        while (_memHistory.Count > 30) _memHistory.Dequeue();

        var panel = new Panel(BuildMetrics(cpu, mem, gc, threads, uptime, handleCount))
        {
            Header = new PanelHeader("[green]Health Dashboard[/]"),
            Border = BoxBorder.Rounded
        };
        return panel;
    }

    private IRenderable BuildMetrics(double cpu, double mem, double gc, int threads, TimeSpan uptime, int handles)
    {
        var grid = new Grid().AddColumn().AddColumn();

        grid.AddRow(new Markup("[cyan]CPU:[/]"), new Markup($"[yellow]{cpu:F1}%[/] [grey]{Environment.ProcessorCount} cores[/]"));
        grid.AddRow(new Markup("[cyan]Memory:[/]"), new Markup($"[yellow]{mem:F0}MB[/] [grey](GC: {gc:F0}MB)[/]"));
        grid.AddRow(new Markup("[cyan]Threads:[/]"), new Markup($"[white]{threads}[/]"));
        grid.AddRow(new Markup("[cyan]Handles:[/]"), new Markup($"[white]{handles}[/]"));
        grid.AddRow(new Markup("[cyan]Uptime:[/]"), new Markup($"[white]{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s[/]"));

        var cpuSpark = string.Join("", _cpuHistory.Select(v => v > 80 ? "[red]█[/]" : v > 50 ? "[yellow]█[/]" : "[green]█[/]"));
        var memSpark = string.Join("", _memHistory.Select(v => v > 500 ? "[red]█[/]" : v > 200 ? "[yellow]█[/]" : "[green]█[/]"));

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("[cyan]CPU History (30s):[/]");
        sb.AppendLine(cpuSpark);
        sb.AppendLine();
        sb.AppendLine("[cyan]Memory History (30s):[/]");
        sb.AppendLine(memSpark);

        grid.AddRow(new Markup(sb.ToString()), new Markup(""));

        return grid;
    }

    private double GetCpuUsage()
    {
        var now = DateTime.UtcNow;
        var totalTicks = _process.TotalProcessorTime.Ticks;
        var elapsed = (now - _lastCpuTime).TotalMilliseconds;
        var used = (totalTicks - _lastTotalProcessorTime) / (double)Stopwatch.Frequency * 1000;

        _lastTotalProcessorTime = totalTicks;
        _lastCpuTime = now;

        return elapsed > 0 ? used / elapsed * 100 : 0;
    }
}
