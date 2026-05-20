using System.Threading.Channels;
using LTAI.Network.Models;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace LTAI.Network.Messaging;

public interface IMessageBus
{
    Task PublishAsync(NetworkMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NetworkMessage>> ConsumeBatchAsync(int maxCount, CancellationToken cancellationToken = default);
    Task SubscribeAsync(Func<NetworkMessage, Task> handler, CancellationToken cancellationToken = default);
    string NodeId { get; }
}

public sealed class MassTransitMessageBus : IMessageBus, IAsyncDisposable
{
    private readonly IBusControl _bus;
    private readonly ILogger<MassTransitMessageBus> _logger;
    private readonly Channel<NetworkMessage> _localChannel;
    private readonly List<Func<NetworkMessage, Task>> _handlers = new();

    public string NodeId { get; }

    public MassTransitMessageBus(
        IBusControl bus,
        ILogger<MassTransitMessageBus> logger)
    {
        _bus = bus;
        _logger = logger;
        NodeId = $"node_{Guid.NewGuid():N}"[..12];
        _localChannel = System.Threading.Channels.Channel.CreateUnbounded<NetworkMessage>();
    }

    public async Task PublishAsync(NetworkMessage message, CancellationToken cancellationToken = default)
    {
        var msg = new NetworkMessage
        {
            MessageId = message.MessageId,
            FromPeer = NodeId,
            ToPeer = message.ToPeer,
            Action = message.Action,
            Payload = message.Payload,
            Timestamp = message.Timestamp,
            Priority = message.Priority,
            TtlMs = message.TtlMs
        };

        try
        {
            await _bus.Publish(new LTAINetworkMessage
            {
                MessageId = msg.MessageId,
                FromPeer = msg.FromPeer,
                ToPeer = msg.ToPeer,
                Action = msg.Action,
                Payload = msg.Payload,
                Timestamp = msg.Timestamp,
                Priority = msg.Priority,
                TtlMs = msg.TtlMs
            }, cancellationToken);

            _logger.LogDebug("MT published: {Action} -> {To}", msg.Action, msg.ToPeer ?? "broadcast");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MassTransit publish failed, falling back to local channel");
            await _localChannel.Writer.WriteAsync(msg, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<NetworkMessage>> ConsumeBatchAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        var messages = new List<NetworkMessage>();
        while (messages.Count < maxCount && _localChannel.Reader.TryRead(out var msg))
        {
            messages.Add(msg);
        }

        return messages.AsReadOnly();
    }

    public async Task SubscribeAsync(Func<NetworkMessage, Task> handler, CancellationToken cancellationToken = default)
    {
        _handlers.Add(handler);

        _ = Task.Run(async () =>
        {
            await foreach (var msg in _localChannel.Reader.ReadAllAsync(cancellationToken))
            {
                foreach (var h in _handlers)
                {
                    try { await h(msg); }
                    catch { /* non-fatal */ }
                }
            }
        }, cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _bus.StartAsync(cancellationToken);
        _logger.LogInformation("MassTransit bus started: {NodeId}", NodeId);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _localChannel.Writer.TryComplete();
        await _bus.StopAsync(cancellationToken);
        _logger.LogInformation("MassTransit bus stopped: {NodeId}", NodeId);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}

public sealed class LTAINetworkMessage
{
    public string MessageId { get; init; } = string.Empty;
    public string FromPeer { get; init; } = string.Empty;
    public string? ToPeer { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? Payload { get; init; }
    public DateTime Timestamp { get; init; }
    public string Priority { get; init; } = "normal";
    public int TtlMs { get; init; } = 30000;
}
