using System.Text.Json;
using LTAI.Agent.Feedback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LTAI.Web;

public static class FeedbackEndpoints
{
    private static readonly JsonSerializerOptions _jsonCaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapFeedbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/feedback", async (
            HttpContext context,
            FeedbackCollector collector,
            ILogger<FeedbackCollector> logger,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<FeedbackRequest>(body, _jsonCaseInsensitive);

                if (request == null || string.IsNullOrWhiteSpace(request.AgentName))
                {
                    context.Response.StatusCode = 400;
                    return Results.Json(new { error = "AgentName is required" });
                }

                var sentiment = request.Sentiment?.ToLowerInvariant() switch
                {
                    "positive" or "👍" or "up" => FeedbackSentiment.Positive,
                    "negative" or "👎" or "down" => FeedbackSentiment.Negative,
                    _ => FeedbackSentiment.Neutral
                };

                var entry = new FeedbackEntry
                {
                    AgentName = request.AgentName,
                    SessionId = request.SessionId ?? "",
                    UserQuery = request.UserQuery ?? "",
                    AgentResponse = request.AgentResponse ?? "",
                    Sentiment = sentiment,
                    Comment = request.Comment
                };

                collector.RecordFeedback(entry);

                logger.LogInformation("Feedback API: agent={Agent} sentiment={Sentiment}",
                    request.AgentName, sentiment);

                return Results.Json(new
                {
                    success = true,
                    feedback_id = entry.Id,
                    agent = entry.AgentName,
                    sentiment = sentiment.ToString()
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Feedback endpoint error");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapGet("/api/feedback/quality/{agentName}", (
            string agentName,
            FeedbackCollector collector) =>
        {
            var score = collector.GetQualityScore(agentName);
            return Results.Json(new
            {
                agent = score.AgentName,
                quality_score = score.QualityScore,
                positive_rate = score.PositiveRate,
                total_feedback = score.TotalFeedback,
                positive_count = score.PositiveCount,
                negative_count = score.NegativeCount,
                neutral_count = score.NeutralCount,
                last_updated = score.LastUpdated
            });
        });

        endpoints.MapGet("/api/feedback/quality", (FeedbackCollector collector) =>
        {
            var scores = collector.GetAllQualityScores();
            return Results.Json(new
            {
                agents = scores.Select(kv => new
                {
                    agent = kv.Value.AgentName,
                    quality_score = kv.Value.QualityScore,
                    positive_rate = kv.Value.PositiveRate,
                    total_feedback = kv.Value.TotalFeedback
                })
            });
        });

        endpoints.MapGet("/api/feedback/recent/{agentName}", (
            string agentName,
            FeedbackCollector collector,
            int? count) =>
        {
            var feedback = collector.GetRecentFeedback(agentName, count ?? 10);
            return Results.Json(new
            {
                agent = agentName,
                count = feedback.Count,
                feedback = feedback.Select(f => new
                {
                    id = f.Id,
                    sentiment = f.Sentiment.ToString(),
                    comment = f.Comment,
                    timestamp = f.Timestamp
                })
            });
        });
    }
}

public sealed record FeedbackRequest
{
    public string AgentName { get; init; } = "";
    public string? SessionId { get; init; }
    public string? UserQuery { get; init; }
    public string? AgentResponse { get; init; }
    public string? Sentiment { get; init; }
    public string? Comment { get; init; }
}
