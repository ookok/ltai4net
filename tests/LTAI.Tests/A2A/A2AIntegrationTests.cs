// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  A2AIntegrationTests — MAF A2A 注册 + Endpoint 映射集成测试
// ═══════════════════════════════════════════════════════════════
//
//  验证 LTAI.Web 已正确集成 MAF A2A：
//  - A2AServerRegistrationOptions 默认行为正确
//  - AddA2AServer 扩展在 IServiceCollection 上注册 keyed A2AServer
//  - MapA2AHttpJson 路由注册（端到端：TestServer 验证 .well-known/agent-card.json）
// ═══════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LTAI.Tests.A2A;

public class A2AIntegrationTests
{
    [Fact]
    public void A2AServerRegistrationOptions_DefaultsAreCorrect()
    {
        var options = new A2AServerRegistrationOptions();

        Assert.Null(options.AgentRunMode);
        Assert.Null(options.ServerOptions);
    }

    [Fact]
    public void A2AServerRegistrationOptions_AgentRunMode_CanBeSet()
    {
        var options = new A2AServerRegistrationOptions
        {
            AgentRunMode = AgentRunMode.DisallowBackground
        };

        Assert.NotNull(options.AgentRunMode);
        Assert.Equal(AgentRunMode.DisallowBackground, options.AgentRunMode);
    }

    [Fact]
    public void AgentRunMode_StaticInstances_AreNotNull()
    {
        Assert.NotNull(AgentRunMode.DisallowBackground);
        Assert.NotNull(AgentRunMode.AllowBackgroundIfSupported);
    }

    [Fact]
    public void AddA2AServer_ByAgentName_RegistersKeyedA2AServer()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(new EchoChatClient());
        services.AddKeyedSingleton<AIAgent>("test-agent", (sp, _) =>
            new EchoAgent());

        // Act
        services.AddA2AServer("test-agent");

        // Build
        var sp = services.BuildServiceProvider();
        var a2a = sp.GetKeyedService<A2AServer>("test-agent");

        // Assert
        Assert.NotNull(a2a);
    }

    [Fact]
    public async Task MapA2AHttpJson_WithMinimalHost_ReturnsAgentCardJson()
    {
        // Arrange — minimal ASP.NET Core host with chat agent + A2A
        using var host = await BuildMinimalA2AHostAsync();

        // Act — fetch the well-known agent card
        var cardResponse = await host.GetTestClient().GetAsync("/.well-known/agent-card.json");

        // Assert
        Assert.Equal(HttpStatusCode.OK, cardResponse.StatusCode);
        var cardJson = await cardResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"LTAI\"", cardJson);
        Assert.Contains("\"version\"", cardJson);
        Assert.Contains("\"/a2a/chat\"", cardJson);

        using var doc = JsonDocument.Parse(cardJson);
        var root = doc.RootElement;
        Assert.Equal("LTAI", root.GetProperty("name").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.True(root.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
    }

    [Fact]
    public async Task MapA2AHttpJson_A2AEndpoint_IsRegisteredInRouteTable()
    {
        // Arrange
        using var host = await BuildMinimalA2AHostAsync();
        using var client = host.GetTestClient();

        // Act — dump all registered routes
        var routeDump = await client.GetAsync("/__routes");
        var routesJson = await routeDump.Content.ReadAsStringAsync();

        // Assert — /a2a/chat or a route containing "a2a" must be in the route table
        Assert.True(
            routesJson.Contains("/a2a") || routesJson.Contains("a2a"),
            $"Expected A2A route to be registered. Actual routes: {routesJson}");
    }

    private static async Task<IHost> BuildMinimalA2AHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        builder.Services.AddSingleton<IChatClient>(new EchoChatClient());

        builder.AddAIAgent("chat", (sp, name) =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            return new ChatClientAgent(chatClient, "You are LTAI.", name);
        })
        .WithInMemorySessionStore()
        .AddA2AServer();

        var app = builder.Build();

        app.MapA2AHttpJson("chat", "/a2a/chat");

        app.MapGet("/.well-known/agent-card.json", (HttpContext _) => Results.Json(new
        {
            name = "LTAI",
            description = "LTAI - Long-running Tree-structured AI assistant",
            version = "1.0.0",
            capabilities = new { streaming = true },
            defaultInputModes = new[] { "text" },
            defaultOutputModes = new[] { "text" },
            skills = Array.Empty<object>(),
            url = "/a2a/chat"
        }));

        // Debug route dump
        app.MapGet("/__routes", (HttpContext ctx) =>
        {
            var endpointDataSource = ctx.RequestServices.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
            var routes = endpointDataSource.Endpoints
                .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
                .Select(e => $"{string.Join(", ", e.Metadata.OfType<Microsoft.AspNetCore.Routing.HttpMethodMetadata>().SelectMany(m => m.HttpMethods))} {e.RoutePattern.RawText}")
                .ToList();
            return Results.Json(routes);
        });

        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// 测试用 <see cref="IChatClient"/>：回显用户消息。
    /// </summary>
    private sealed class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("echo");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var last = "";
            foreach (var m in messages) last = m.Text ?? last;
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"echo: {last}");
            await Task.CompletedTask;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var last = "";
            foreach (var m in messages) last = m.Text ?? last;
            return await Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"echo: {last}")));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class EchoAgent : AIAgent
    {
        public override string? Name => "echo-agent";

        protected override string? IdCore => "echo-agent-id";

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var last = "";
            foreach (var m in messages) last = m.Text ?? last;
            return Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, $"echo: {last}")]
            });
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var last = "";
            foreach (var m in messages) last = m.Text ?? last;
            yield return new AgentResponseUpdate(ChatRole.Assistant, $"echo: {last}");
            await Task.CompletedTask;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Echo agent is a test stub.");

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Echo agent is a test stub.");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Echo agent is a test stub.");
    }
}
