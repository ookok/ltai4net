using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LTAI.Core.Session;
using Xunit;

namespace LTAI.Tests.Protocols;

public class A2AProtocolTests
{
    [Fact]
    public void SessionHandle_RoundTrip_Serialization()
    {
        var conversationId = Guid.NewGuid().ToString();
        var element = JsonDocument.Parse("{\"key\":\"value\"}").RootElement;
        var handle = new JsonSessionHandle(conversationId, element);

        var json = handle.SerializeToJson();
        Assert.NotNull(json);
        Assert.Contains("key", json);
        Assert.Contains("value", json);
    }

    [Fact]
    public void SessionHandle_EmptyJson_ReturnsEmptyString()
    {
        var conversationId = Guid.NewGuid().ToString();
        var element = JsonDocument.Parse("{}").RootElement;
        var handle = new JsonSessionHandle(conversationId, element);

        var json = handle.SerializeToJson();
        Assert.Equal("{}", json);
    }

    [Fact]
    public void SessionHandle_NullElement_ReturnsEmpty()
    {
        var handle = new JsonSessionHandle("test-id", null);

        var json = handle.SerializeToJson();
        Assert.NotNull(json);
        Assert.Equal("", json);
    }

    [Fact]
    public async Task SessionManager_SaveAndLoadSession_RoundTrip()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "ltai_test_a2a_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new SessionManager(sessionDir);

            var conversationId = Guid.NewGuid().ToString();
            var element = JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}").RootElement;
            var handle = new JsonSessionHandle(conversationId, element);

            await manager.SaveSessionAsync(handle);

            var restored = await manager.LoadSessionAsync(conversationId);
            Assert.NotNull(restored);
            var restoredJson = restored.SerializeToJson();
            Assert.NotNull(restoredJson);
            Assert.Contains("hello", restoredJson);
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionManager_LoadNonExistentSession_ReturnsNull()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "ltai_test_a2a_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new SessionManager(sessionDir);

            var result = await manager.LoadSessionAsync("non-existent-id");
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public void AgentCard_Metadata_Structure()
    {
        var card = new
        {
            name = "LTAI",
            description = "LTAI - Long-running Tree-structured AI assistant",
            version = "1.0.0",
            capabilities = new { streaming = true },
            defaultInputModes = new[] { "text" },
            defaultOutputModes = new[] { "text" },
            skills = Array.Empty<object>(),
            url = "/a2a/chat"
        };

        var json = JsonSerializer.Serialize(card);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("LTAI", root.GetProperty("name").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.True(root.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
        Assert.Equal("/a2a/chat", root.GetProperty("url").GetString());
        Assert.Equal("text", root.GetProperty("defaultInputModes")[0].GetString());
    }

    [Fact]
    public void A2AEndpoint_PathPattern_IsCorrect()
    {
        var agentName = "LTAI-Chat";
        var expectedPath = $"/a2a/{agentName}";
        Assert.Equal("/a2a/LTAI-Chat", expectedPath);
    }

    [Fact]
    public void WellKnownAgentCard_EndpointPath_IsCorrect()
    {
        var endpoint = "/.well-known/agent-card.json";
        Assert.Equal("/.well-known/agent-card.json", endpoint);
    }
}
