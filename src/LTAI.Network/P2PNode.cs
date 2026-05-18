using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LTAI.Core.Configuration;
using LTAI.Network.Interfaces;
using LTAI.Network.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Network;

public sealed class P2PNode : IP2PNode, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<P2PNode> _logger;
    private readonly ConcurrentDictionary<string, PeerInfo> _knownPeers = new();
    private readonly Channel<NetworkMessage> _messageChannel;
    private CancellationTokenSource? _cts;

    public string PeerId { get; }

    public P2PNode(
        IOptions<LTAIOptions> options,
        ILogger<P2PNode> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _options = options;
        _logger = logger;
        PeerId = GeneratePeerId();
        _messageChannel = System.Threading.Channels.Channel.CreateUnbounded<NetworkMessage>();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("P2P Node started: {PeerId}", PeerId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _messageChannel.Writer.TryComplete();
        _logger.LogInformation("P2P Node stopped: {PeerId}", PeerId);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(NetworkMessage message, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Sending message: {Action} -> {ToPeer}", message.Action, message.ToPeer ?? "broadcast");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PeerInfo>> GetKnownPeersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<PeerInfo>>(_knownPeers.Values.ToList());
    }

    public async IAsyncEnumerable<NetworkMessage> ReceiveMessagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _messageChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    public async Task RegisterPeerAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        _knownPeers[peer.PeerId] = peer;
        _logger.LogInformation("Peer registered: {PeerId} at {Address}:{Port}", peer.PeerId, peer.Address, peer.Port);

        if (!string.IsNullOrEmpty(_options.Value.Network.DiscoveryEndpoint))
        {
            await AnnounceToDiscoveryAsync(peer, cancellationToken);
        }
    }

    private async Task AnnounceToDiscoveryAsync(PeerInfo peer, CancellationToken cancellationToken)
    {
        try
        {
            var request = new DiscoveryRequest
            {
                PeerId = peer.PeerId,
                Address = peer.Address,
                Port = peer.Port
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(
                $"{_options.Value.Network.DiscoveryEndpoint.TrimEnd('/')}/api/discovery/announce",
                content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var discoveryResponse = JsonSerializer.Deserialize<DiscoveryResponse>(responseJson);
                if (discoveryResponse?.KnownPeers != null)
                {
                    foreach (var p in discoveryResponse.KnownPeers)
                        _knownPeers[p.PeerId] = p;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to announce to discovery endpoint");
        }
    }

    private static string GeneratePeerId()
    {
        var random = new Random();
        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
