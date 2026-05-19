using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Market.Intel;
using LTAI.Market.Opportunity;
using LTAI.Market.Profiling;
using LTAI.Market.Revenue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Market;

public static class MarketEndpoints
{
    public static void MapMarketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/market/profile/build", async (
            HttpContext context,
            UserProfileEngine engine,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<ProfileBuildRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Role))
                return Results.Json(new { error = "role is required" }, statusCode: 400);

            var profile = engine.Build(request.Role, request.CollectedData ?? new Dictionary<string, object?>());
            return Results.Json(profile);
        });

        endpoints.MapGet("/api/market/profile/{role}", async (
            string role,
            UserProfileEngine engine) =>
        {
            var profile = await engine.LoadAsync(role);
            if (profile == null)
                return Results.Json(new { error = $"Profile not found for role: {role}" }, statusCode: 404);

            return Results.Json(profile);
        });

        endpoints.MapPost("/api/market/profile/update", async (
            HttpContext context,
            UserProfileEngine engine,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<ProfileUpdateRequest>(body);

            if (request == null || request.Profile == null)
                return Results.Json(new { error = "profile is required" }, statusCode: 400);

            engine.Update(request.Profile, request.Announcement ?? new Dictionary<string, object?>());
            return Results.Json(new { success = true });
        });

        endpoints.MapGet("/api/market/competitors/report", (UserProfileEngine engine) =>
        {
            return Results.Ok(engine.GetCompetitorReport);
        });

        endpoints.MapPost("/api/market/opportunity/score", async (
            HttpContext context,
            OpportunityScorer scorer,
            UserProfileEngine profileEngine,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<OpportunityScoreRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Role))
                return Results.Json(new { error = "role is required" }, statusCode: 400);

            var profile = await profileEngine.LoadAsync(request.Role);
            if (profile == null)
                return Results.Json(new { error = $"Profile not found for role: {request.Role}" }, statusCode: 404);

            var results = scorer.Score(profile, request.Announcements ?? new List<Dictionary<string, object?>>());
            return Results.Json(results);
        });

        endpoints.MapPost("/api/market/opportunity/trend", async (
            HttpContext context,
            MarketTrendAnalyzer analyzer,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<OpportunityTrendRequest>(body);

            if (request == null)
                return Results.Json(new { error = "announcements is required" }, statusCode: 400);

            var report = analyzer.Analyze(request.Announcements ?? new List<Dictionary<string, object?>>());
            return Results.Json(report);
        });

        endpoints.MapPost("/api/market/bid/strategy", async (
            HttpContext context,
            BiddingAssistant assistant,
            UserProfileEngine profileEngine,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<BidStrategyRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Role))
                return Results.Json(new { error = "role is required" }, statusCode: 400);

            var profile = await profileEngine.LoadAsync(request.Role);
            if (profile == null)
                return Results.Json(new { error = $"Profile not found for role: {request.Role}" }, statusCode: 404);

            var strategy = assistant.GenerateBidStrategy(profile, request.Opportunity,
                request.Competitors ?? new List<Competitor>());
            return Results.Ok(strategy);
        });

        endpoints.MapPost("/api/market/bid/proposal", async (
            HttpContext context,
            BiddingAssistant assistant,
            UserProfileEngine profileEngine,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<BidProposalRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Role))
                return Results.Json(new { error = "role is required" }, statusCode: 400);

            var profile = await profileEngine.LoadAsync(request.Role);
            if (profile == null)
                return Results.Json(new { error = $"Profile not found for role: {request.Role}" }, statusCode: 404);

            var proposal = assistant.GenerateTechnicalProposalOutline(profile, request.ProjectTitle ?? "");
            return Results.Ok(proposal);
        });

        endpoints.MapPost("/api/market/revenue/record", async (
            HttpContext context,
            RevenueEngine engine,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<RevenueRecordRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Category))
                return Results.Json(new { error = "category is required" }, statusCode: 400);

            engine.Record(request.Category, request.Description ?? "",
                request.Value ?? 0f, request.Confidence ?? 1.0f);
            return Results.Json(new { success = true });
        });

        endpoints.MapPost("/api/market/revenue/cost", async (
            HttpContext context,
            RevenueEngine engine,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<RevenueCostRequest>(body);

            if (request == null)
                return Results.Json(new { error = "request body required" }, statusCode: 400);

            engine.RecordCost(request.ApiCalls ?? 0, request.StorageGb ?? 0f, request.ComputeHours ?? 0f);
            return Results.Json(new { success = true });
        });

        endpoints.MapGet("/api/market/revenue/report", (RevenueEngine engine) =>
        {
            var report = engine.MonthlyReport();
            return Results.Json(report);
        });

        endpoints.MapGet("/api/market/revenue/stats", (RevenueEngine engine) =>
        {
            var stats = engine.GetStats();
            return Results.Json(stats);
        });

        endpoints.MapPost("/api/market/intel/detect", async (
            HttpContext context,
            ListedCompanyIntel intel,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var request = JsonSerializer.Deserialize<IntelDetectRequest>(body);

            if (request == null)
                return Results.Json(new { error = "announcements is required" }, statusCode: 400);

            var signals = intel.Detect(request.Announcements ?? new List<Dictionary<string, object?>>());
            return Results.Json(signals);
        });

        endpoints.MapGet("/api/market/intel/report", (ListedCompanyIntel intel) =>
        {
            var signals = intel.Detect(new List<Dictionary<string, object?>>());
            var report = intel.GenerateReport(signals);
            return Results.Ok(report);
        });

        endpoints.MapGet("/api/market/investment/options", (SelfInvestmentEngine engine) =>
        {
            var options = engine.EvaluateOptions();
            return Results.Json(options);
        });

        endpoints.MapGet("/api/market/investment/recommend", (SelfInvestmentEngine engine) =>
        {
            var recommendation = engine.Recommend();
            return Results.Ok(recommendation);
        });
    }
}

public sealed record ProfileBuildRequest(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("collected_data")] Dictionary<string, object?>? CollectedData
);

public sealed record ProfileUpdateRequest(
    [property: JsonPropertyName("profile")] UserProfile? Profile,
    [property: JsonPropertyName("announcement")] Dictionary<string, object?>? Announcement
);

public sealed record OpportunityScoreRequest(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("announcements")] List<Dictionary<string, object?>>? Announcements
);

public sealed record OpportunityTrendRequest(
    [property: JsonPropertyName("announcements")] List<Dictionary<string, object?>>? Announcements
);

public sealed record BidStrategyRequest(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("opportunity")] ScoredOpportunity Opportunity,
    [property: JsonPropertyName("competitors")] List<Competitor>? Competitors
);

public sealed record BidProposalRequest(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("project_title")] string? ProjectTitle
);

public sealed record RevenueRecordRequest(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("value")] float? Value,
    [property: JsonPropertyName("confidence")] float? Confidence
);

public sealed record RevenueCostRequest(
    [property: JsonPropertyName("api_calls")] int? ApiCalls,
    [property: JsonPropertyName("storage_gb")] float? StorageGb,
    [property: JsonPropertyName("compute_hours")] float? ComputeHours
);

public sealed record IntelDetectRequest(
    [property: JsonPropertyName("announcements")] List<Dictionary<string, object?>>? Announcements
);
