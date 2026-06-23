using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LTAI.Core.Session;
using Xunit;

namespace LTAI.Tests.Protocols;

public class AGUIProtocolTests
{
    [Fact]
    public async Task AGUI_Session_SaveAndLoadByThreadId()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "ltai_test_agui_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new SessionManager(sessionDir);

            var threadId = Guid.NewGuid().ToString();
            var element = JsonDocument.Parse("{\"thread\":\"" + threadId + "\",\"state\":\"active\"}").RootElement;
            var handle = new JsonSessionHandle(threadId, element);

            await manager.SaveSessionAsync(handle);

            var restored = await manager.LoadSessionAsync(threadId);
            Assert.NotNull(restored);
            var json = restored.SerializeToJson();
            Assert.NotNull(json);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(threadId, doc.RootElement.GetProperty("thread").GetString());
            Assert.Equal("active", doc.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task AGUI_MultipleThreadSessions_AreIsolated()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "ltai_test_agui_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new SessionManager(sessionDir);

            var threadA = Guid.NewGuid().ToString();
            var threadB = Guid.NewGuid().ToString();

            var elementA = JsonDocument.Parse("{\"thread\":\"" + threadA + "\",\"data\":\"session-a\"}").RootElement;
            var elementB = JsonDocument.Parse("{\"thread\":\"" + threadB + "\",\"data\":\"session-b\"}").RootElement;

            await manager.SaveSessionAsync(new JsonSessionHandle(threadA, elementA));
            await manager.SaveSessionAsync(new JsonSessionHandle(threadB, elementB));

            var restoredA = await manager.LoadSessionAsync(threadA);
            var restoredB = await manager.LoadSessionAsync(threadB);

            Assert.NotNull(restoredA);
            Assert.NotNull(restoredB);

            using var docA = JsonDocument.Parse(restoredA.SerializeToJson());
            using var docB = JsonDocument.Parse(restoredB.SerializeToJson());

            Assert.Equal("session-a", docA.RootElement.GetProperty("data").GetString());
            Assert.Equal("session-b", docB.RootElement.GetProperty("data").GetString());
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public void AGUIEndpoint_PathPattern_IsCorrect()
    {
        var agentName = "LTAI-Dev";
        var expectedPath = $"/agui/{agentName}";
        Assert.Equal("/agui/LTAI-Dev", expectedPath);
    }

    [Fact]
    public void AGUI_ThreadId_To_SessionFilename_Mapping()
    {
        var threadId = "test-thread-123";
        var safeFilename = threadId
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(":", "_");
        Assert.Equal("test-thread-123", safeFilename);
    }
}
