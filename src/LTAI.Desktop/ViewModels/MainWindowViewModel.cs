using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LTAI.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly Process _process = Process.GetCurrentProcess();
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private DateTime _startTime = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private TimeSpan _lastCpuTime = TimeSpan.Zero;

    public int ViewCount { get; }

    [ObservableProperty]
    private int _activeIndex = 1;

    [ObservableProperty]
    private string _statusRight = "CPU: --  MEM: --";

    [ObservableProperty]
    private string _statusLeft = "\u25cc  LTAI";

    [ObservableProperty]
    private string _statusTooltip = "";

    [ObservableProperty]
    private string _capsuleText = "🤖 -- | 🔥 -- | 🌿 --";

    [ObservableProperty]
    private bool _sidebarCollapsed;

    public MainWindowViewModel(int viewCount = 7)
    {
        ViewCount = viewCount;
    }

    public void RefreshStatus()
    {
        _process.Refresh();
        var now = DateTime.UtcNow;
        var cpuTime = _process.TotalProcessorTime;
        var elapsed = (now - _lastCpuSample).TotalSeconds;
        var cpuUsage = elapsed > 0.5
            ? (cpuTime - _lastCpuTime).TotalSeconds / (Environment.ProcessorCount * elapsed) * 100
            : 0.0;
        _lastCpuSample = now;
        _lastCpuTime = cpuTime;
        var mem = _process.WorkingSet64 / 1024.0 / 1024.0;
        var uptime = now - _startTime;

        var hasKey = LTAI.Core.Configuration.KnownKeys.All
            .Any(k => LTAI.Core.Configuration.SecretManager.Has(k.EnvVar));
        StatusLeft = hasKey ? "\U0001f7e2  LTAI" : "\U0001f534  LTAI";
        StatusRight = string.Format("CPU: {0:F1}%  MEM: {1:F0}MB", cpuUsage, mem);
        StatusTooltip = string.Format(
            "CPU: {0:F1}% | MEM: {1:F0}MB | Threads: {2} | Gen0: {3} | Uptime: {4:D2}:{5:D2}:{6:D2}",
            cpuUsage, mem, _process.Threads.Count,
            System.GC.CollectionCount(0),
            uptime.Hours, uptime.Minutes, uptime.Seconds);
    }

    public void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    public bool TryActivate(int index)
    {
        if (index < 0 || index >= ViewCount) return false;
        ActiveIndex = index;
        return true;
    }
}
