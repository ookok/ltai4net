using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using LTAI.Core.Configuration;

namespace LTAI.Desktop.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private static readonly Process _process = Process.GetCurrentProcess();
    private readonly string _mode;
    private readonly string _dnaStatus;
    private readonly string _safetyPosture;
    private readonly int _maxTokens;

    public DashboardViewModel(string mode, string dnaStatus, string safetyPosture, int maxTokens)
    {
        _mode = mode;
        _dnaStatus = dnaStatus;
        _safetyPosture = safetyPosture;
        _maxTokens = maxTokens;
    }

    public DashboardViewModel(LTAIService svc) : this(svc.Mode, svc.DNAStatus, svc.SafetyPosture, svc.Options.AI.MaxTokens)
    {
    }

    [ObservableProperty]
    private string _sysInfo = "";

    [ObservableProperty]
    private string _healthInfo = "";

    [ObservableProperty]
    private string _sessionInfo = "";

    [ObservableProperty]
    private double _contextRatio;

    [ObservableProperty]
    private string _contextLabel = "";

    [ObservableProperty]
    private double _cacheHitRate;

    [ObservableProperty]
    private string _cacheLabel = "";

    [ObservableProperty]
    private string _devUiStatus = "";

    [ObservableProperty]
    private bool _devUiStatusVisible;

    public void Refresh()
    {
        _process.Refresh();
        SysInfo = $"模式: {_mode}\nDNA: {_dnaStatus}\n安全: {_safetyPosture}\nPID: {_process.Id}\n运行: {(_process.StartTime != default ? DateTime.Now - _process.StartTime : TimeSpan.Zero):hh\\:mm\\:ss}";
        HealthInfo = $"GC 内存: {GC.GetTotalMemory(false) / 1024 / 1024} MB\n线程: {ThreadPool.ThreadCount}\n.NET: {Environment.Version}";
        SessionInfo = $"模型: {UsageTracker.ActiveModel}\n"
                    + $"Token: {UsageTracker.PromptTokens:N0}+{UsageTracker.CompletionTokens:N0}={UsageTracker.TotalTokens:N0}\n"
                    + $"请求: {UsageTracker.Requests}\n"
                    + $"费用: {UsageTracker.CostDisplay}\n"
                    + $"运行: {UsageTracker.Uptime:hh':'mm':'ss}\n"
                    + $"余额: {UsageTracker.BalanceDisplay}";

        ContextRatio = UsageTracker.ContextRatio(_maxTokens) * 100;
        ContextLabel = $"上下文容量: {UsageTracker.ContextText(_maxTokens)}";

        var totalCalls = UsageTracker.CacheHits + UsageTracker.CacheMisses;
        var hitRate = totalCalls > 0 ? UsageTracker.CacheHitRate : 0;
        CacheHitRate = hitRate;
        CacheLabel = $"缓存命中: {hitRate:F1}% ({UsageTracker.CacheHits}/{totalCalls})";
    }

    public void SetDevUiStatus(string message, bool visible)
    {
        DevUiStatus = message;
        DevUiStatusVisible = visible;
    }
}
