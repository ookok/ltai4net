// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ProactiveSuggestService — background suggestion aggregator
//
//  Inspiration: TIDE (arXiv 2606.04743)
//
//  Runs code issue detectors in the background when the user is
//  idle. Aggregates results and exposes them for the DevUI and
//  pipeline step.
//
//  Lifecycle:
//    1. StartAsync (on app start) — register detectors
//    2. TickAsync (background loop) — scan when idle
//    3. GetSuggestions (public API) — return aggregated results
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Suggestions;

/// <summary>
/// Background service that aggregates code issue detections.
/// Runs on a timer (default every 5 minutes when idle).
/// Notifications are consumed by ProactiveSuggestStep or DevUI.
/// </summary>
public sealed class ProactiveSuggestService : BackgroundService, IDisposable
{
    private readonly List<ICodeIssueDetector> _detectors = [];
    private readonly string _workspacePath;
    private readonly ILogger<ProactiveSuggestService> _logger;
    private readonly TimeSpan _idleInterval;
    private IReadOnlyList<CodeIssue>? _lastAggregated;
    private DateTime _lastActivity = DateTime.UtcNow;

    /// <summary>
    /// Fired when new aggregated suggestions are available.
    /// </summary>
    public event Action<IReadOnlyList<CodeIssue>>? OnSuggestionsUpdated;

    /// <summary>Last aggregated results.</summary>
    public IReadOnlyList<CodeIssue>? LastResults => _lastAggregated;

    /// <summary>Registered detector names.</summary>
    public IReadOnlyList<string> DetectorNames =>
        _detectors.Select(d => d.Name).ToList().AsReadOnly();

    public ProactiveSuggestService(
        ILogger<ProactiveSuggestService>? logger = null,
        string? workspacePath = null,
        TimeSpan? idleInterval = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProactiveSuggestService>.Instance;
        _workspacePath = workspacePath ?? Directory.GetCurrentDirectory();
        _idleInterval = idleInterval ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Register a code issue detector.</summary>
    public void RegisterDetector(ICodeIssueDetector detector)
    {
        _detectors.Add(detector);
        _logger.LogInformation("ProactiveSuggest: registered detector '{Name}'", detector.Name);
    }

    /// <summary>
    /// Mark that the user is active (to prevent scanning).
    /// </summary>
    public void MarkActive() => _lastActivity = DateTime.UtcNow;

    /// <summary>
    /// Get all current suggestions, optionally filtered by category.
    /// </summary>
    public IReadOnlyList<CodeIssue> GetSuggestions(string? category = null)
    {
        if (_lastAggregated == null) return [];
        if (category == null) return _lastAggregated;
        return _lastAggregated.Where(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Force a scan immediately.
    /// </summary>
    public async Task<IReadOnlyList<CodeIssue>> ScanNowAsync(CancellationToken ct = default)
    {
        var allIssues = new List<CodeIssue>();
        foreach (var detector in _detectors)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var issues = await detector.ScanAsync(_workspacePath, ct).ConfigureAwait(false);
                allIssues.AddRange(issues);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProactiveSuggest: detector '{Name}' failed", detector.Name);
            }
        }
        _lastAggregated = allIssues.OrderBy(i => i.Severity).ThenBy(i => i.File).ToList();
        OnSuggestionsUpdated?.Invoke(_lastAggregated);
        _logger.LogInformation("ProactiveSuggest: scan completed, {Count} issues found", _lastAggregated.Count);
        return _lastAggregated;
    }

    /// <summary>Has the user been idle long enough to scan?</summary>
    private bool IsIdle => DateTime.UtcNow - _lastActivity > _idleInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProactiveSuggestService: started (interval={Interval})", _idleInterval);

        // Initial scan after a warmup delay
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        if (!stoppingToken.IsCancellationRequested)
            await ScanNowAsync(stoppingToken).ConfigureAwait(false);

        // Periodic scanning loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_idleInterval, stoppingToken).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested) break;

            if (IsIdle)
            {
                _logger.LogDebug("ProactiveSuggest: user idle, scanning...");
                await ScanNowAsync(stoppingToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogTrace("ProactiveSuggest: user active, skipping scan");
            }
        }
    }

    public new void Dispose()
    {
        base.Dispose();
        foreach (var d in _detectors)
            d.Dispose();
    }
}
