using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public enum ReviewStatus
{
    Pending,
    Approved,
    Rejected,
    RequiresRevision,
    TimedOut,
    Escalated
}

public enum TimeoutPolicy
{
    AutoApprove,
    Reject,
    Escalate
}

public sealed class ReviewTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string AgentSource { get; init; } = "";
    public string Reviewer { get; init; } = "";
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;
    public string? Feedback { get; set; }
    public string? RejectionReason { get; set; }
    public double QualityScore { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public DateTime SLADeadline { get; set; }
    public TimeSpan SLATimeout { get; set; } = TimeSpan.FromHours(1);
    public TimeoutPolicy TimeoutPolicy { get; set; } = TimeoutPolicy.Escalate;
    public Dictionary<string, object?> Metadata { get; init; } = new();
}

public sealed class HumanInTheLoopReview
{
    private readonly ILogger<HumanInTheLoopReview> _logger;
    private readonly ConcurrentDictionary<string, ReviewTask> _pendingReviews = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReviewStatus>> _waiters = new();
    private readonly double _autoApproveThreshold;
    private readonly List<string> _regulatoryAgents = new() { "eia", "eia_critic" };

    public event Action<ReviewTask>? OnReviewTaskCreated;
    public event Action<ReviewTask>? OnReviewTaskCompleted;

    public HumanInTheLoopReview(ILogger<HumanInTheLoopReview> logger, double autoApproveThreshold = 0.85)
    {
        _logger = logger;
        _autoApproveThreshold = autoApproveThreshold;
    }

    public bool RequiresHumanReview(string agentName)
    {
        return _regulatoryAgents.Contains(agentName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// High-risk threshold: operations with risk score above this value
    /// get forced TimeoutPolicy.Reject to prevent silent auto-approval on timeout.
    /// </summary>
    private const double HighRiskThreshold = 0.7;

    public ReviewTask CreateReviewTask(
        string agentName,
        string output,
        double qualityScore,
        Dictionary<string, object?>? metadata = null,
        TimeSpan? slaTimeout = null,
        TimeoutPolicy timeoutPolicy = TimeoutPolicy.Escalate,
        double riskScore = 0.0)
    {
        // Security: high-risk operations MUST be rejected on timeout, never auto-approved
        if (riskScore >= HighRiskThreshold && timeoutPolicy != TimeoutPolicy.Reject)
        {
            _logger.LogWarning(
                "HITL: Forcing TimeoutPolicy.Reject for high-risk operation (risk={RiskScore:F2} >= {Threshold:F2}) on {Agent}",
                riskScore, HighRiskThreshold, agentName);
            timeoutPolicy = TimeoutPolicy.Reject;
        }

        var task = new ReviewTask
        {
            Title = $"{agentName} Output Review",
            Content = output,
            AgentSource = agentName,
            Reviewer = "human",
            QualityScore = qualityScore,
            Metadata = metadata ?? new(),
            SLATimeout = slaTimeout ?? TimeSpan.FromHours(1),
            SLADeadline = DateTime.UtcNow.Add(slaTimeout ?? TimeSpan.FromHours(1)),
            TimeoutPolicy = timeoutPolicy
        };

        if (qualityScore >= _autoApproveThreshold && !RequiresHumanReview(agentName))
        {
            task.Status = ReviewStatus.Approved;
            task.ReviewedAt = DateTime.UtcNow;
            task.Feedback = "Auto-approved (quality score above threshold)";
            _logger.LogInformation("HITL: Auto-approved {Agent} output (score={Score:F2})", agentName, qualityScore);
            OnReviewTaskCompleted?.Invoke(task);
            return task;
        }

        if (RequiresHumanReview(agentName))
        {
            _pendingReviews[task.TaskId] = task;
            _waiters[task.TaskId] = new TaskCompletionSource<ReviewStatus>();

            _logger.LogWarning("HITL: Created human review task {TaskId} for regulatory agent '{Agent}' (score={Score:F2}, SLA={SLA})",
                task.TaskId, agentName, qualityScore, task.SLATimeout);

            OnReviewTaskCreated?.Invoke(task);
        }
        else if (qualityScore < _autoApproveThreshold)
        {
            task.Status = ReviewStatus.RequiresRevision;
            task.Feedback = "Quality score below threshold — requires revision";
            _logger.LogWarning("HITL: Output requires revision for {Agent} (score={Score:F2} < {Threshold:F2})",
                agentName, qualityScore, _autoApproveThreshold);
        }

        return task;
    }

    public async Task<ReviewStatus> WaitForApprovalAsync(string taskId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_waiters.TryGetValue(taskId, out var tcs))
            return ReviewStatus.Approved;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return HandleTimeout(taskId);
        }
    }

    private ReviewStatus HandleTimeout(string taskId)
    {
        if (!_pendingReviews.TryGetValue(taskId, out var task))
            return ReviewStatus.TimedOut;

        var result = task.TimeoutPolicy switch
        {
            TimeoutPolicy.AutoApprove => ReviewStatus.Approved,
            TimeoutPolicy.Reject => ReviewStatus.Rejected,
            TimeoutPolicy.Escalate => ReviewStatus.Escalated,
            _ => ReviewStatus.Escalated
        };

        task.Status = result;
        task.ReviewedAt = DateTime.UtcNow;
        task.Feedback = $"SLA timeout ({task.SLATimeout}) — policy: {task.TimeoutPolicy}";

        _pendingReviews.TryRemove(taskId, out _);
        if (_waiters.TryRemove(taskId, out var tcs))
            tcs.TrySetResult(result);

        _logger.LogWarning("HITL: Task {TaskId} timed out, policy={Policy}, result={Result}",
            taskId, task.TimeoutPolicy, result);

        OnReviewTaskCompleted?.Invoke(task);
        return result;
    }

    public ReviewTask? Approve(string taskId, string? feedback = null)
    {
        if (!_pendingReviews.TryRemove(taskId, out var task))
            return null;

        task.Status = ReviewStatus.Approved;
        task.ReviewedAt = DateTime.UtcNow;
        task.Feedback = feedback ?? "Approved by reviewer";

        if (_waiters.TryRemove(taskId, out var tcs))
            tcs.TrySetResult(ReviewStatus.Approved);

        _logger.LogInformation("HITL: Approved task {TaskId}", taskId);
        OnReviewTaskCompleted?.Invoke(task);
        return task;
    }

    public ReviewTask? Reject(string taskId, string reason)
    {
        if (!_pendingReviews.TryRemove(taskId, out var task))
            return null;

        task.Status = ReviewStatus.Rejected;
        task.ReviewedAt = DateTime.UtcNow;
        task.RejectionReason = reason;
        task.Feedback = $"Rejected: {reason}";

        if (_waiters.TryRemove(taskId, out var tcs))
            tcs.TrySetResult(ReviewStatus.Rejected);

        _logger.LogWarning("HITL: Rejected task {TaskId}: {Reason}", taskId, reason);
        OnReviewTaskCompleted?.Invoke(task);
        return task;
    }

    public IReadOnlyList<ReviewTask> GetPendingReviews()
    {
        return _pendingReviews.Values.OrderBy(t => t.CreatedAt).ToList();
    }

    public IReadOnlyList<ReviewTask> GetOverdueTasks()
    {
        var now = DateTime.UtcNow;
        return _pendingReviews.Values
            .Where(t => t.Status == ReviewStatus.Pending && t.SLADeadline < now)
            .OrderBy(t => t.SLADeadline)
            .ToList();
    }

    public ReviewTask? GetReview(string taskId)
    {
        return _pendingReviews.TryGetValue(taskId, out var task) ? task : null;
    }

    public Dictionary<string, object?> GetStatus()
    {
        var now = DateTime.UtcNow;
        return new()
        {
            ["pending_count"] = _pendingReviews.Count,
            ["overdue_count"] = _pendingReviews.Values.Count(t => t.SLADeadline < now),
            ["auto_approve_threshold"] = _autoApproveThreshold,
            ["regulatory_agents"] = _regulatoryAgents,
            ["pending_tasks"] = _pendingReviews.Values.Select(t => new
            {
                t.TaskId,
                t.AgentSource,
                t.Title,
                t.QualityScore,
                t.CreatedAt,
                t.SLADeadline,
                t.TimeoutPolicy,
                t.Status,
                IsOverdue = t.SLADeadline < now
            }).ToList()
        };
    }
}
