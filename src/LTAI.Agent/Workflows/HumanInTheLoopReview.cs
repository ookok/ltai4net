using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public enum ReviewStatus
{
    Pending,
    Approved,
    Rejected,
    RequiresRevision
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
    public Dictionary<string, object?> Metadata { get; init; } = new();
}

public sealed class HumanInTheLoopReview
{
    private readonly ILogger<HumanInTheLoopReview> _logger;
    private readonly ConcurrentDictionary<string, ReviewTask> _pendingReviews = new();
    private readonly double _autoApproveThreshold;
    private readonly List<string> _regulatoryAgents = new() { "eia", "eia_critic" };

    public HumanInTheLoopReview(ILogger<HumanInTheLoopReview> logger, double autoApproveThreshold = 0.85)
    {
        _logger = logger;
        _autoApproveThreshold = autoApproveThreshold;
    }

    public bool RequiresHumanReview(string agentName)
    {
        return _regulatoryAgents.Contains(agentName, StringComparer.OrdinalIgnoreCase);
    }

    public ReviewTask CreateReviewTask(string agentName, string output, double qualityScore, Dictionary<string, object?>? metadata = null)
    {
        var task = new ReviewTask
        {
            Title = $"{agentName} Output Review",
            Content = output,
            AgentSource = agentName,
            Reviewer = "human",
            QualityScore = qualityScore,
            Metadata = metadata ?? new()
        };

        if (qualityScore >= _autoApproveThreshold && !RequiresHumanReview(agentName))
        {
            task.Status = ReviewStatus.Approved;
            task.ReviewedAt = DateTime.UtcNow;
            task.Feedback = "Auto-approved (quality score above threshold)";
            _logger.LogInformation("HITL: Auto-approved {Agent} output (score={Score:F2})", agentName, qualityScore);
            return task;
        }

        if (RequiresHumanReview(agentName))
        {
            _pendingReviews[task.TaskId] = task;
            _logger.LogWarning("HITL: Created human review task {TaskId} for regulatory agent '{Agent}' (score={Score:F2})",
                task.TaskId, agentName, qualityScore);
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

    public ReviewTask? Approve(string taskId, string? feedback = null)
    {
        if (!_pendingReviews.TryRemove(taskId, out var task))
            return null;

        task.Status = ReviewStatus.Approved;
        task.ReviewedAt = DateTime.UtcNow;
        task.Feedback = feedback ?? "Approved by reviewer";
        _logger.LogInformation("HITL: Approved task {TaskId}", taskId);
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
        _logger.LogWarning("HITL: Rejected task {TaskId}: {Reason}", taskId, reason);
        return task;
    }

    public IReadOnlyList<ReviewTask> GetPendingReviews()
    {
        return _pendingReviews.Values.OrderBy(t => t.CreatedAt).ToList();
    }

    public ReviewTask? GetReview(string taskId)
    {
        return _pendingReviews.TryGetValue(taskId, out var task) ? task : null;
    }

    public Dictionary<string, object?> GetStatus()
    {
        return new()
        {
            ["pending_count"] = _pendingReviews.Count,
            ["auto_approve_threshold"] = _autoApproveThreshold,
            ["regulatory_agents"] = _regulatoryAgents,
            ["pending_tasks"] = _pendingReviews.Values.Select(t => new
            {
                t.TaskId,
                t.AgentSource,
                t.Title,
                t.QualityScore,
                t.CreatedAt,
                t.Status
            }).ToList()
        };
    }
}
