using System.Text.Json;
using LTAI.Network.Interfaces;
using LTAI.Network.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Network.Bridge;

public sealed class A2aP2pBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IP2PNode _p2pNode;
    private readonly ILogger<A2aP2pBridge> _logger;
    private readonly CancellationTokenSource _cts = new();

    public A2aP2pBridge(IP2PNode p2pNode, ILogger<A2aP2pBridge> logger)
    {
        _p2pNode = p2pNode;
        _logger = logger;
        _ = Task.Run(async () =>
        {
            try { await ListenForP2pMessagesAsync(_cts.Token); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex) { _logger.LogError(ex, "P2P message listener failed"); }
        }, _cts.Token).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
                _logger.LogError(t.Exception, "P2P message listener background task failed");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task ForwardA2aRequestToP2pAsync(string agentName, string payload, CancellationToken ct = default)
    {
        var message = new NetworkMessage
        {
            Action = $"a2a.request.{agentName}",
            Payload = payload,
            Priority = "high",
            TtlMs = 60000
        };

        await _p2pNode.SendMessageAsync(message, ct);
        _logger.LogInformation("A2A request forwarded to P2P network: agent={Agent}", agentName);
    }

    public async Task BroadcastAgentStatusAsync(string agentName, string status, CancellationToken ct = default)
    {
        var message = new NetworkMessage
        {
            Action = "a2a.agent.status",
            Payload = JsonSerializer.Serialize(new { agent = agentName, status, timestamp = DateTime.UtcNow }, JsonOpts),
            Priority = "normal"
        };

        await _p2pNode.SendMessageAsync(message, ct);
    }

    private async Task ListenForP2pMessagesAsync(CancellationToken ct)
    {
        await foreach (var message in _p2pNode.ReceiveMessagesAsync(ct))
        {
            try
            {
                if (message.Action.StartsWith("a2a.request."))
                {
                    _logger.LogInformation("Received A2A request via P2P: action={Action} from={FromPeer}",
                        message.Action, message.FromPeer);
                }
                else
                {
                    _logger.LogDebug("Received P2P message: action={Action} from={FromPeer}",
                        message.Action, message.FromPeer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing P2P message in A2A bridge");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
