// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P15 fan-out for hot-reload events. Subscribers: TUI status line, Desktop
/// WorkflowsPanel, Web <c>/api/workflows/events</c> SSE stream. The notifier
/// is intentionally fire-and-forget — slow subscribers cannot back-pressure
/// the <see cref="YAMLWorkflowRegistry"/> reload loop (D68: old workflow
/// keeps running; new workflow swap is non-blocking).
/// </summary>
public sealed class WorkflowHotReloadNotifier
{
    // P15.5: emit reload events as OTel activities so the P9.1
    // DevUISpanCollector (and the OTel console/OTLP exporters wired in
    // LTAI.Core) pick them up automatically — no extra wiring needed.
    private static readonly ActivitySource Activity = new("LTAI.Workflows");

    private readonly ConcurrentDictionary<Guid, IWorkflowSubscriber> _subscribers = new();
    private readonly ILogger<WorkflowHotReloadNotifier> _logger;

    public WorkflowHotReloadNotifier(ILogger<WorkflowHotReloadNotifier> logger)
    {
        _logger = logger;
    }

    /// <summary>Subscribe to reload events. Returns a token used to unsubscribe.</summary>
    public Guid Subscribe(IWorkflowSubscriber subscriber)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = subscriber;
        return id;
    }

    /// <summary>Unsubscribe by token. Safe to call multiple times.</summary>
    public void Unsubscribe(Guid token) => _subscribers.TryRemove(token, out _);

    /// <summary>Publish a successful reload. All subscribers are notified.</summary>
    public void PublishReloaded(WorkflowReloadEvent evt)
    {
        _logger.LogInformation(
            "Workflow reloaded: {Name} ({Type}) v{Version}, {Subs} subscribers",
            evt.Name, evt.Type, evt.Version, _subscribers.Count);
        using var activity = Activity.StartActivity("workflow.reloaded", ActivityKind.Internal);
        activity?.SetTag("workflow.name", evt.Name);
        activity?.SetTag("workflow.type", evt.Type);
        activity?.SetTag("workflow.version", evt.Version);
        activity?.SetTag("workflow.path", evt.FilePath);
        foreach (var sub in _subscribers.Values)
        {
            try { sub.OnReloaded(evt); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscriber threw on OnReloaded for {Name}", evt.Name);
            }
        }
    }

    /// <summary>Publish a failed reload. The old workflow remains active.</summary>
    public void PublishLoadFailed(WorkflowLoadFailedEvent evt)
    {
        _logger.LogError(
            "Workflow reload FAILED: {Name} ({Type}) — {Reason}. Old workflow preserved.",
            evt.Name, evt.Type, evt.Reason);
        using var activity = Activity.StartActivity("workflow.reload_failed", ActivityKind.Internal);
        activity?.SetTag("workflow.name", evt.Name);
        activity?.SetTag("workflow.type", evt.Type);
        activity?.SetTag("workflow.path", evt.FilePath);
        activity?.SetTag("workflow.reason", evt.Reason);
        activity?.SetStatus(ActivityStatusCode.Error, evt.Reason);
        foreach (var sub in _subscribers.Values)
        {
            try { sub.OnLoadFailed(evt); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscriber threw on OnLoadFailed for {Name}", evt.Name);
            }
        }
    }
}

/// <summary>Subscriber contract for hot-reload events.</summary>
public interface IWorkflowSubscriber
{
    void OnReloaded(WorkflowReloadEvent evt);
    void OnLoadFailed(WorkflowLoadFailedEvent evt);
}

/// <summary>Successful reload event payload.</summary>
/// <param name="Name">Workflow name (file stem, e.g. <c>decision-tree</c>).</param>
/// <param name="Type">Workflow type (<c>decision-tree</c>, <c>sequential</c>, etc.).</param>
/// <param name="Version">Schema version field from the file.</param>
/// <param name="ReloadedAtUtc">When the reload completed.</param>
/// <param name="FilePath">Absolute path to the source file.</param>
public readonly record struct WorkflowReloadEvent(
    string Name,
    string Type,
    int Version,
    DateTime ReloadedAtUtc,
    string FilePath);

/// <summary>Failed reload event payload. Old workflow remains active.</summary>
/// <param name="Name">Workflow name (file stem).</param>
/// <param name="Type">Workflow type.</param>
/// <param name="FilePath">Absolute path to the source file.</param>
/// <param name="Reason">Human-readable failure reason (parse error, IO error, etc.).</param>
/// <param name="FailedAtUtc">When the failure occurred.</param>
public readonly record struct WorkflowLoadFailedEvent(
    string Name,
    string Type,
    string FilePath,
    string Reason,
    DateTime FailedAtUtc);
