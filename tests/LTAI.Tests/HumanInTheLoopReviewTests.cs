using LTAI.Agent.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class HumanInTheLoopReviewTests
{
    private readonly ILogger<HumanInTheLoopReview> _logger = NullLogger<HumanInTheLoopReview>.Instance;

    [Fact]
    public void HighQualityScore_AutoApproves()
    {
        var hitl = new HumanInTheLoopReview(_logger, autoApproveThreshold: 0.85);
        var task = hitl.CreateReviewTask("code", "def foo(): pass", 0.95, null);

        Assert.Equal(ReviewStatus.Approved, task.Status);
        Assert.NotNull(task.ReviewedAt);
        Assert.Contains("Auto-approved", task.Feedback);
    }

    [Fact]
    public void LowQualityScore_RequiresRevision()
    {
        var hitl = new HumanInTheLoopReview(_logger, autoApproveThreshold: 0.85);
        var task = hitl.CreateReviewTask("chat", "some output", 0.5, null);

        Assert.Equal(ReviewStatus.RequiresRevision, task.Status);
        Assert.Contains("below threshold", task.Feedback);
    }

    [Fact]
    public void RegulatoryAgent_AlwaysRequiresHumanReview()
    {
        var hitl = new HumanInTheLoopReview(_logger, autoApproveThreshold: 0.85);
        var task = hitl.CreateReviewTask("eia", "EIA report content here", 0.99, null);

        Assert.Equal(ReviewStatus.Pending, task.Status);
        Assert.Equal("human", task.Reviewer);
    }

    [Fact]
    public void EiaCritic_AlwaysRequiresHumanReview()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var task = hitl.CreateReviewTask("eia_critic", "review content", 0.92, null);

        Assert.Equal(ReviewStatus.Pending, task.Status);
    }

    [Fact]
    public void ApproveTask_UpdatesStatus()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var task = hitl.CreateReviewTask("eia", "content", 0.7, null);
        Assert.Equal(ReviewStatus.Pending, task.Status);

        var approved = hitl.Approve(task.TaskId, "Looks good");
        Assert.NotNull(approved);
        Assert.Equal(ReviewStatus.Approved, approved!.Status);
        Assert.Contains("Looks good", approved.Feedback);
        Assert.NotNull(approved.ReviewedAt);
    }

    [Fact]
    public void RejectTask_UpdatesStatusWithReason()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var task = hitl.CreateReviewTask("eia", "content", 0.6, null);

        var rejected = hitl.Reject(task.TaskId, "Missing standard references");
        Assert.NotNull(rejected);
        Assert.Equal(ReviewStatus.Rejected, rejected!.Status);
        Assert.Contains("Missing standard references", rejected.RejectionReason);
    }

    [Fact]
    public void ApproveUnknownTask_ReturnsNull()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var result = hitl.Approve("nonexistent-id");
        Assert.Null(result);
    }

    [Fact]
    public void RejectUnknownTask_ReturnsNull()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var result = hitl.Reject("nonexistent-id", "reason");
        Assert.Null(result);
    }

    [Fact]
    public void GetPendingReviews_ReturnsOnlyPending()
    {
        var hitl = new HumanInTheLoopReview(_logger);

        hitl.CreateReviewTask("eia", "r1", 0.7, null);
        hitl.CreateReviewTask("eia_critic", "r2", 0.8, null);
        var autoApproved = hitl.CreateReviewTask("chat", "r3", 0.95, null);

        Assert.Equal(ReviewStatus.Approved, autoApproved.Status);

        var pending = hitl.GetPendingReviews();
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public void ApproveRemovesFromPending()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var task = hitl.CreateReviewTask("eia", "content", 0.7, null);

        Assert.Single(hitl.GetPendingReviews());

        hitl.Approve(task.TaskId);

        Assert.Empty(hitl.GetPendingReviews());
    }

    [Fact]
    public void GetStatus_ReturnsCorrectCounts()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        hitl.CreateReviewTask("eia", "r1", 0.7, null);
        hitl.CreateReviewTask("eia", "r2", 0.8, null);
        hitl.CreateReviewTask("chat", "r3", 0.95, null);

        var status = hitl.GetStatus();
        Assert.Equal(2, status["pending_count"]);
        Assert.Equal(0.85, status["auto_approve_threshold"]);
        Assert.NotNull(status["regulatory_agents"]);
    }

    [Fact]
    public void RequiresHumanReview_ChecksRegulatoryAgents()
    {
        var hitl = new HumanInTheLoopReview(_logger);

        Assert.True(hitl.RequiresHumanReview("eia"));
        Assert.True(hitl.RequiresHumanReview("eia_critic"));
        Assert.False(hitl.RequiresHumanReview("chat"));
        Assert.False(hitl.RequiresHumanReview("code"));
    }

    [Fact]
    public void GetReview_ReturnsTaskById()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var task = hitl.CreateReviewTask("eia", "content", 0.7, null);

        var found = hitl.GetReview(task.TaskId);
        Assert.NotNull(found);
        Assert.Equal(task.TaskId, found!.TaskId);
    }

    [Fact]
    public void Metadata_StoredAndAccessible()
    {
        var hitl = new HumanInTheLoopReview(_logger);
        var metadata = new Dictionary<string, object?>
        {
            ["report_id"] = "RPT-001",
            ["generated_by"] = "EIAAgent"
        };
        var task = hitl.CreateReviewTask("eia", "content", 0.7, metadata);

        Assert.Equal("RPT-001", task.Metadata["report_id"]);
        Assert.Equal("EIAAgent", task.Metadata["generated_by"]);
    }
}
