using System.Text.Json;
using LTAI.Agent.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;

namespace LTAI.Web;

public static class ParliamentEndpoints
{
    public static void MapParliamentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/parliament/convene", async (
            HttpContext context,
            AgentParliament parliament,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var request = JsonSerializer.Deserialize<ParliamentRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                {
                    context.Response.StatusCode = 400;
                    return Results.Json(new { error = "Query is required" });
                }

                var messages = new List<ChatMessage> { new(ChatRole.User, request.Query) };
                var result = await parliament.ConveneAsync(
                    messages,
                    overrideVoterAgents: request.Voters,
                    criticAgent: request.Critic,
                    requiredPassVotes: request.RequiredPassVotes ?? 2,
                    cancellationToken: cancellationToken);

                return Results.Json(new
                {
                    verdict = result.Verdict.ToString(),
                    final_response = result.FinalResponse,
                    total_agents = result.TotalAgents,
                    passed_votes = result.PassedVotes,
                    rejected_votes = result.RejectedVotes,
                    consensus_score = result.ConsensusScore,
                    votes = result.Votes.Select(v => new
                    {
                        agent = v.AgentName,
                        intent = v.Intent,
                        confidence = v.Confidence,
                        verdict = v.Verdict,
                        reasoning = v.Reasoning?[..Math.Min(v.Reasoning?.Length ?? 0, 300)],
                        weight = v.Weight
                    })
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapPost("/api/parliament/complex", async (
            HttpContext context,
            AgentParliament parliament,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var request = JsonSerializer.Deserialize<ParliamentRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                {
                    context.Response.StatusCode = 400;
                    return Results.Json(new { error = "Query is required" });
                }

                // For complex topics, use all agents including EIA
                var voters = request.Voters ?? "chat,code,reasoning,eia";
                var messages = new List<ChatMessage> { new(ChatRole.User, request.Query) };
                var result = await parliament.ConveneAsync(
                    messages,
                    overrideVoterAgents: voters,
                    criticAgent: request.Critic ?? "eia_critic",
                    requiredPassVotes: request.RequiredPassVotes ?? 3,
                    cancellationToken: cancellationToken);

                return Results.Json(new
                {
                    verdict = result.Verdict.ToString(),
                    final_response = result.FinalResponse,
                    total_agents = result.TotalAgents,
                    passed_votes = result.PassedVotes,
                    rejected_votes = result.RejectedVotes,
                    consensus_score = result.ConsensusScore,
                    summary = result.Summary
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });
    }
}

public sealed record ParliamentRequest
{
    public string Query { get; init; } = "";
    public string? Voters { get; init; }
    public string? Critic { get; init; }
    public int? RequiredPassVotes { get; init; }
}
