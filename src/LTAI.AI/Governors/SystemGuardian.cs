using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class SystemGuardian
{
    private readonly IChatClient _llm;
    private readonly ILogger<SystemGuardian> _logger;
    private readonly object _lock = new();
    private SystemMode _mode = SystemMode.Normal;
    private int _errorCount;
    private CancellationTokenSource? _monitorCts;

    public SystemMode Mode
    {
        get { lock (_lock) return _mode; }
        private set { lock (_lock) _mode = value; }
    }

    public SystemGuardian(IChatClient llm, ILogger<SystemGuardian> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public void StartMonitoring(TimeSpan interval)
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();
        _ = MonitorLoopAsync(interval, _monitorCts.Token);
        _logger.LogInformation("Guardian monitoring started at {Interval}s interval", interval.TotalSeconds);
    }

    public void StopMonitoring()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _logger.LogInformation("Guardian monitoring stopped");
    }

    public void RecordError()
    {
        Interlocked.Increment(ref _errorCount);
        var current = _errorCount;

        if (current > 50)
            DegradeTo(SystemMode.LifeSupport);
        else if (current > 20)
            DegradeTo(SystemMode.Degraded);
    }

    public void ResetErrors()
    {
        Interlocked.Exchange(ref _errorCount, 0);
        if (Mode != SystemMode.Normal)
        {
            Mode = SystemMode.Normal;
            _logger.LogInformation("Guardian restored to Normal mode");
        }
    }

    public async Task<string> EmergencyChatAsync(string query, CancellationToken cancellationToken = default)
    {
        _logger.LogCritical("Emergency chat activated in {Mode} mode", Mode);
        try
        {
            return await _llm.CompleteAsync(
                $"System is in {Mode} mode. Emergency query: {query}",
                new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 2048 },
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency chat failed");
            return "Emergency system unavailable. Please try again later.";
        }
    }

    private void DegradeTo(SystemMode newMode)
    {
        var oldMode = Mode;
        Mode = newMode;
        _logger.LogWarning("Guardian degraded: {OldMode} -> {NewMode} (errors: {ErrorCount})", oldMode, newMode, _errorCount);
    }

    private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                var memMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;

                if (memMb > 8192)
                    DegradeTo(SystemMode.LifeSupport);
                else if (_errorCount > 50)
                    DegradeTo(SystemMode.LifeSupport);
                else if (_errorCount > 20)
                    DegradeTo(SystemMode.Degraded);
                else if (Mode != SystemMode.Normal && _errorCount < 5)
                    ResetErrors();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
