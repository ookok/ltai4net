using LTAI.Agent.Agents;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class AgentTemplateTests
{
    private readonly LTAIAgentCard _card = new()
    {
        Name = "AgentTemplate",
        Type = AgentType.Chat,
        Instructions = "Test agent",
        Middleware = new() { "unified_safety" },
        Tools = new()
    };

    [Fact]
    public void TC01_Constructor_DoesNotThrow()
    {
        var skills = new SkillRegistry();
        var ex = Record.Exception(() =>
        {
            var agent = new AgentTemplate(_card,
                new FakeChatClient(),
                skills,
                NullLogger<AgentTemplate>.Instance);
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task TC02_RunAsync_ReturnsResponse()
    {
        var skills = new SkillRegistry();
        var fakeClient = new FakeChatClient();
        fakeClient.AddRoute("*", _ => "Hello, I am AgentTemplate!");

        var agent = new AgentTemplate(_card, fakeClient, skills, NullLogger<AgentTemplate>.Instance);
        var response = await agent.RunAsync(
            new[] { new ChatMessage(ChatRole.User, "hello") },
            null, null, CancellationToken.None);

        Assert.NotNull(response.Text);
        Assert.True(response.Text.Length > 0);
    }

    [Fact]
    public async Task TC03_StrategyPattern_DefaultStrategyHandlesAll()
    {
        var skills = new SkillRegistry();
        var fakeClient = new FakeChatClient();
        fakeClient.AddRoute("*", _ => "ok");

        var agent = new AgentTemplate(_card, fakeClient, skills, NullLogger<AgentTemplate>.Instance);
        var response = await agent.RunAsync(
            new[] { new ChatMessage(ChatRole.User, "any query") },
            null, null, CancellationToken.None);

        Assert.Contains("ok", response.Text);
    }
}
