using LTAI.Core.Messaging;
using Xunit;

namespace LTAI.Tests;

public class MessageBusTests
{
    [Fact]
    public async Task SendAsync_RoutesTypedMessage_ToRegisteredHandler()
    {
        var bus = new MessageBus();
        bus.RegisterHandler<ClassifyQuery, ClassificationResult>(
            (msg, ct) => Task.FromResult(new ClassificationResult { Label = "deep", Query = msg.Query }));

        var result = await bus.SendAsync<ClassifyQuery, ClassificationResult>(
            new ClassifyQuery { Query = "test" });

        Assert.Equal("deep", result.Label);
        Assert.Equal("test", result.Query);
    }

    [Fact]
    public async Task SendAsync_ThrowsWhenNoHandler()
    {
        var bus = new MessageBus();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.SendAsync<ClassifyQuery, ClassificationResult>(new ClassifyQuery()));
    }

    [Fact]
    public async Task BroadcastAsync_DeliversToAllSubscribers()
    {
        var bus = new MessageBus();
        var received = new List<string>();
        bus.Subscribe<ClassifyQuery>((msg, ct) => { received.Add($"s1:{msg.Query}"); return Task.CompletedTask; });
        bus.Subscribe<ClassifyQuery>((msg, ct) => { received.Add($"s2:{msg.Query}"); return Task.CompletedTask; });

        await bus.BroadcastAsync(new ClassifyQuery { Query = "hello" });

        Assert.Equal(2, received.Count);
        Assert.Contains("s1:hello", received);
        Assert.Contains("s2:hello", received);
    }

    [Fact]
    public async Task BroadcastAsync_NoSubscribers_DoesNotThrow()
    {
        var bus = new MessageBus();
        await bus.BroadcastAsync(new ClassifyQuery { Query = "silent" });
    }

    [Fact]
    public void HasHandler_ReturnsTrue_WhenRegistered()
    {
        var bus = new MessageBus();
        bus.RegisterHandler<SelectProvider, ProviderResult>((msg, ct) =>
            Task.FromResult(new ProviderResult { Model = "test" }));

        Assert.True(bus.HasHandler<SelectProvider>());
        Assert.False(bus.HasHandler<ReviewOutput>());
    }
}
