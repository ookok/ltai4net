using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace LTAI.MAF;

public sealed class A2AAgentConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Uri? Endpoint { get; set; }
    public string Instructions { get; set; } = "";
    public List<string> SupportedProtocols { get; set; } = new() { "http+json", "jsonrpc" };
}

public sealed class A2AHostConfig
{
    public string BaseUrl { get; set; } = "https://localhost:5001";
    public List<A2AAgentConfig> Agents { get; set; } = new();
    public bool EnableDiscovery { get; set; } = true;
    public string WellKnownPath { get; set; } = "/.well-known/agent-card.json";
}

public static class A2AV1Extensions
{
    public static IServiceCollection AddA2AAgent(this IServiceCollection services, string name, A2AAgentConfig config)
    {
        services.AddKeyedSingleton(name, config);
        return services;
    }

    public static void MapA2AV1Endpoints(this IEndpointRouteBuilder endpoints, A2AHostConfig config)
    {
        if (config.EnableDiscovery)
        {
            endpoints.MapGet(config.WellKnownPath, async (HttpContext context) =>
            {
                var cards = config.Agents.Select(a => new
                {
                    name = a.Name,
                    description = a.Description,
                    url = $"{config.BaseUrl}/a2a/{a.Name}",
                    protocolVersion = "1.0",
                    supportedInterfaces = a.SupportedProtocols.Select(p => new
                    {
                        protocolBinding = p,
                        protocolVersion = "1.0"
                    })
                });

                var agentCard = new
                {
                    agents = cards,
                    defaultProtocol = "http+json",
                    version = "1.0"
                };

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(agentCard, new JsonSerializerOptions { WriteIndented = true }));
            });
        }

        foreach (var agent in config.Agents)
        {
            var agentName = agent.Name;
            endpoints.MapPost($"/a2a/{agentName}", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    agent = agentName,
                    response = $"A2A v1 response from {agentName}: {agent.Description}",
                    protocol = "http+json",
                    version = "1.0"
                }));
            });
        }
    }
}
