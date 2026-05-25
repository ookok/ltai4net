using System.Collections.Concurrent;
using System.Net.Http.Json;
using LTAI.Infra.Network.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Network;

/// <summary>
/// Gossip-based decentralized peer discovery for P2P network.
/// Each node maintains a partial view and periodically exchanges peers.
/// </summary>
public sealed class GossipDiscovery
{
    private readonly IP2PNode _p2pNode;
    private readonly HttpClient _http;
    private readonly ILogger<GossipDiscovery> _logger;
    private readonly ConcurrentDictionary<string, (string Address, int Port, DateTime LastSeen)> _peers = new();
    private const int MaxPeers = 20;
    private const int GossipFanout = 3;

    public GossipDiscovery(IP2PNode p2pNode, IHttpClientFactory httpFactory, ILogger<GossipDiscovery> logger)
    {
        _p2pNode = p2pNode;
        _http = httpFactory.CreateClient("p2p");
        _logger = logger;
    }

    public async Task RunGossipCycleAsync(CancellationToken ct)
    {
        var knownPeers = await _p2pNode.GetKnownPeersAsync(ct).ConfigureAwait(false);
        foreach (var peer in knownPeers.Take(GossipFanout))
        {
            try
            {
                var url = $"http://{peer.Address}:{peer.Port}/p2p/gossip";
                var myPeers = _peers.Values.Select(p => new { p.Address, p.Port }).ToList();
                var response = await _http.PostAsJsonAsync(url, new
                {
                    from = _p2pNode.PeerId,
                    peers = myPeers,
                    timestamp = DateTime.UtcNow
                }, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var remotePeers = await response.Content.ReadFromJsonAsync<GossipResponse>(cancellationToken: ct).ConfigureAwait(false);
                    if (remotePeers?.Peers != null)
                        foreach (var rp in remotePeers.Peers)
                            _peers[rp.Id] = (rp.Address, rp.Port, DateTime.UtcNow);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Gossip to {Peer} failed", peer.PeerId); }
        }

        PruneStale(TimeSpan.FromMinutes(10));
    }

    public void ReceiveGossip(string fromPeer, List<(string Id, string Address, int Port)> peers)
    {
        foreach (var (id, addr, port) in peers)
            if (id != _p2pNode.PeerId)
                _peers[id] = (addr, port, DateTime.UtcNow);
    }

    private void PruneStale(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var key in _peers.Keys)
            if (_peers.TryGetValue(key, out var p) && p.LastSeen < cutoff)
                _peers.TryRemove(key, out _);
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["peers"] = _peers.Count,
        ["max_peers"] = MaxPeers,
        ["fanout"] = GossipFanout
    };
}

public sealed record GossipResponse
{
    public List<PeerEntry> Peers { get; init; } = new();
}

public sealed record PeerEntry
{
    public string Id { get; init; } = "";
    public string Address { get; init; } = "";
    public int Port { get; init; }
}
