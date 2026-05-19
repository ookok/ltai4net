using System.Text.Json;
using LTAI.Network.Acceleration;
using LTAI.Network.Bridge;
using LTAI.Network.Consensus;
using LTAI.Network.Links;
using LTAI.Network.Perception;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Network;

public static class NetworkEndpoints
{
    public static void MapNetworkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/network/distributed", () =>
            Results.Json(DistributedConsciousness.Instance.Stats()));

        endpoints.MapPost("/api/network/distributed/merge", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<DistributedMergeRequest>(body);

            if (request == null)
                return Results.Json(new { error = "SelfModel and traits required" }, statusCode: 400);

            var selfModel = request.SelfModel ?? new SelfModelSnapshot();
            var insights = request.RecentInsights ?? new List<string>();
            var mutations = request.Mutations ?? new List<string>();

            var fragment = DistributedConsciousness.Instance.PrepareFragment(
                selfModel, insights, mutations, request.EmergencePhase);

            DistributedConsciousness.Instance.ReceiveFragment(fragment);
            var merged = DistributedConsciousness.Instance.MergeExperiences(fragment);

            return Results.Json(new { fragment_id = fragment.FragmentId, merged });
        });

        endpoints.MapGet("/api/network/swarm/status", () =>
            Results.Json(SwarmCoordinator.Instance.Stats()));

        endpoints.MapGet("/api/network/nat", () =>
            Results.Json(NATTraverser.Instance.Stats()));

        endpoints.MapGet("/api/network/reputation", () =>
            Results.Json(Reputation.Instance.GetAllScores()));

        endpoints.MapPost("/api/network/reputation/rate", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ReputationRateRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.PeerId))
                return Results.Json(new { error = "PeerId required" }, statusCode: 400);

            Reputation.Instance.RatePeer(request.PeerId, request.Delta);
            var score = Reputation.Instance.GetScore(request.PeerId);

            return Results.Json(new { peer_id = request.PeerId, score });
        });

        endpoints.MapGet("/api/network/offline", () =>
            Results.Json(DualMode.Instance.GetStatus()));

        endpoints.MapGet("/api/network/biometric/profile/{userId}", (string userId) =>
        {
            var profile = BiometricRegistry.Instance.GetProfile(userId);
            if (profile == null)
                return Results.Json(new { error = "Profile not found" }, statusCode: 404);

            return Results.Json(profile);
        });

        endpoints.MapPost("/api/network/biometric/verify", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<BiometricVerifyRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                return Results.Json(new { error = "UserId required" }, statusCode: 400);

            var confidence = BiometricRegistry.Instance.VerifyIdentity(request.UserId);
            return Results.Json(new { user_id = request.UserId, identity_confidence = confidence });
        });

        endpoints.MapGet("/api/network/spatial", () =>
            Results.Json(new { report = SpatialAwareness.Instance.GetSpatialReport() }));

        endpoints.MapGet("/api/network/presence", () =>
            Results.Json(new { report = P2PPresence.Instance.GetReport() }));

        endpoints.MapGet("/api/network/reach/devices", () =>
            Results.Json(ReachGateway.Instance.GetDevices()));

        endpoints.MapPost("/api/network/reach/sensor", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ReachSensorPostRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.DeviceId))
                return Results.Json(new { error = "DeviceId required" }, statusCode: 400);

            var sensorRequest = new SensorRequest
            {
                SensorType = request.SensorType,
                Instruction = request.Instruction,
                Priority = request.Priority,
                TimeoutMs = request.TimeoutMs,
                Required = request.Required,
                Context = request.Context
            };

            var response = await ReachGateway.Instance.RequestSensor(
                request.DeviceId, sensorRequest, cancellationToken);

            return Results.Json(new { device_id = request.DeviceId, response });
        });

        endpoints.MapGet("/api/network/bridge/channels", () =>
            Results.Json(ChannelBridge.Instance.GetActiveChannels()));

        endpoints.MapPost("/api/network/fetch", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<FetchRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Url))
                return Results.Json(new { error = "Url required" }, statusCode: 400);

            var result = await NetworkResilience.Instance.ResilientFetchAsync(request.Url, cancellationToken);
            return Results.Json(result);
        });

        endpoints.MapGet("/api/network/mirrors", () =>
            Results.Json(NetworkResilience.Instance.GetStats()));

        endpoints.MapPost("/api/network/external/search", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<ExternalSearchRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return Results.Json(new { error = "Query required" }, statusCode: 400);

            var results = await ExternalAccess.Instance.DeepSearchAsync(
                request.Query, request.MaxResults ?? 20, cancellationToken);

            return Results.Json(results);
        });

        endpoints.MapPost("/api/network/external/fetch-paper", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<FetchPaperRequest>(body);

            if (request == null || (string.IsNullOrWhiteSpace(request.Doi) && string.IsNullOrWhiteSpace(request.Url)))
                return Results.Json(new { error = "Doi or Url required" }, statusCode: 400);

            var result = await ExternalAccess.Instance.FetchPaperAsync(request.Doi, request.Url, cancellationToken);

            if (result == null)
                return Results.Json(new { error = "Paper not found" }, statusCode: 404);

            return Results.Json(new { content = result });
        });

        endpoints.MapPost("/api/network/external/dns", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<DnsRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Domain))
                return Results.Json(new { error = "Domain required" }, statusCode: 400);

            var record = await ExternalAccess.Instance.DnsResolveAsync(request.Domain, cancellationToken);
            return Results.Json(record);
        });

        endpoints.MapGet("/api/network/external/strategies", () =>
            Results.Json(ExternalAccess.Instance.GetStrategies()));
    }
}

public sealed record DistributedMergeRequest
{
    public SelfModelSnapshot? SelfModel { get; init; }
    public List<string>? RecentInsights { get; init; }
    public List<string>? Mutations { get; init; }
    public string EmergencePhase { get; init; } = string.Empty;
}

public sealed record ReputationRateRequest
{
    public string PeerId { get; init; } = string.Empty;
    public double Delta { get; init; }
}

public sealed record BiometricVerifyRequest
{
    public string UserId { get; init; } = string.Empty;
}

public sealed record ReachSensorPostRequest
{
    public string DeviceId { get; init; } = string.Empty;
    public SensorType SensorType { get; init; }
    public string Instruction { get; init; } = string.Empty;
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public int TimeoutMs { get; init; } = 30000;
    public bool Required { get; init; }
    public string? Context { get; init; }
}

public sealed record FetchRequest
{
    public string Url { get; init; } = string.Empty;
}

public sealed record ExternalSearchRequest
{
    public string Query { get; init; } = string.Empty;
    public int? MaxResults { get; init; }
}

public sealed record FetchPaperRequest
{
    public string? Doi { get; init; }
    public string? Url { get; init; }
}

public sealed record DnsRequest
{
    public string Domain { get; init; } = string.Empty;
}
