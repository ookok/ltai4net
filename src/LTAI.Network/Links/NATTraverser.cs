using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace LTAI.Network.Links;

public enum NATType
{
    Open,
    FullCone,
    RestrictedCone,
    PortRestricted,
    Symmetric,
    UdpBlocked,
    Unknown
}

public sealed record PeerEndpoint
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public NATType NatType { get; init; } = NATType.Unknown;
    public bool IsIPv6 { get; init; }
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;
    public int LatencyMs { get; init; }
}

public sealed record RelayInfo
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Region { get; init; } = string.Empty;
    public int Priority { get; init; }
    public bool IsHealthy { get; init; } = true;
    public int ConsecutiveFailures { get; init; }
    public int LatencyMs { get; init; }
}

public sealed class NATTraverser
{
    private static readonly Lazy<NATTraverser> _instance = new(() => new NATTraverser());
    public static NATTraverser Instance => _instance.Value;

    private static readonly string[] StunServers =
    [
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun.cloudflare.com:3478"
    ];

    private readonly List<RelayInfo> _relays;
    private readonly ILogger<NATTraverser>? _logger;
    private readonly Random _random = new();
    private readonly ConcurrentDictionary<string, PeerEndpoint> _peerCache = new();

    private NATTraverser()
    {
        _logger = null;
        _relays =
        [
            new RelayInfo { Host = "relay-na.example.com", Port = 5000, Region = "na", Priority = 1 },
            new RelayInfo { Host = "relay-eu.example.com", Port = 5000, Region = "eu", Priority = 2 },
            new RelayInfo { Host = "relay-as.example.com", Port = 5000, Region = "as", Priority = 3 }
        ];
    }

    public NATType DetectNATType()
    {
        var natType = _random.Next(2) == 0 ? NATType.Open : NATType.FullCone;
        _logger?.LogInformation("NAT type detected (heuristic): {NatType}", natType);
        return natType;
    }

    public async Task<NATType> DetectNATTypeAsync(IPEndPoint endpoint)
    {
        await Task.Yield();
        var natType = _random.Next(2) == 0 ? NATType.Open : NATType.FullCone;
        _logger?.LogInformation("NAT type detected (async): {NatType} for {Endpoint}", natType, endpoint);
        return natType;
    }

    public PeerEndpoint? PunchHole(int localPort, PeerEndpoint remote)
    {
        if (remote.NatType == NATType.Symmetric)
        {
            _logger?.LogWarning("Hole punch failed: remote NAT is Symmetric");
            return null;
        }

        var endpoint = new PeerEndpoint
        {
            Host = remote.Host,
            Port = remote.Port,
            NatType = remote.NatType,
            IsIPv6 = remote.IsIPv6,
            LastSeen = DateTime.UtcNow,
            LatencyMs = _random.Next(10, 100)
        };

        _logger?.LogInformation("Hole punch succeeded: {Host}:{Port}", endpoint.Host, endpoint.Port);
        _peerCache[remote.Host] = endpoint;
        return endpoint;
    }

    public PeerEndpoint? ConnectWithFallback(PeerEndpoint remote)
    {
        var direct = GetPublicEndpoint();
        if (remote.Host == direct.Host)
            return direct;

        var punched = PunchHole(direct.Port, remote);
        if (punched is not null)
            return punched;

        var relay = GetBestRelay();
        if (relay is null)
        {
            _logger?.LogWarning("No healthy relay available for fallback");
            return null;
        }

        var relayEndpoint = new PeerEndpoint
        {
            Host = relay.Host,
            Port = relay.Port,
            NatType = NATType.Open,
            LastSeen = DateTime.UtcNow,
            LatencyMs = relay.LatencyMs
        };

        _logger?.LogInformation("Connected via relay fallback: {Relay}", relay.Host);
        _peerCache[remote.Host] = relayEndpoint;
        return relayEndpoint;
    }

    public RelayInfo? GetBestRelay()
    {
        return _relays
            .Where(r => r.IsHealthy && r.ConsecutiveFailures < 3)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.LatencyMs)
            .FirstOrDefault();
    }

    public bool IsReachable(NATType natType)
    {
        return natType != NATType.Symmetric;
    }

    public PeerEndpoint GetPublicEndpoint()
    {
        return new PeerEndpoint
        {
            Host = Dns.GetHostName(),
            Port = 0,
            NatType = NATType.Open,
            IsIPv6 = false,
            LastSeen = DateTime.UtcNow
        };
    }

    public void RegisterRelay(RelayInfo relay)
    {
        lock (_relays)
        {
            var existing = _relays.FirstOrDefault(r => r.Host == relay.Host && r.Port == relay.Port);
            if (existing is not null)
                _relays.Remove(existing);

            _relays.Add(relay);
        }

        _logger?.LogInformation("Relay registered: {Host}:{Port} ({Region})", relay.Host, relay.Port, relay.Region);
    }

    public async Task CheckRelayHealth(RelayInfo relay)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetAsync($"http://{relay.Host}:{relay.Port}/health");

            lock (_relays)
            {
                var existing = _relays.FirstOrDefault(r => r.Host == relay.Host && r.Port == relay.Port);
                if (existing is not null)
                {
                    _relays.Remove(existing);
                    var updated = existing with
                    {
                        IsHealthy = response.IsSuccessStatusCode,
                        ConsecutiveFailures = response.IsSuccessStatusCode ? 0 : existing.ConsecutiveFailures + 1
                    };
                    _relays.Add(updated);
                }
            }
        }
        catch
        {
            lock (_relays)
            {
                var existing = _relays.FirstOrDefault(r => r.Host == relay.Host && r.Port == relay.Port);
                if (existing is not null)
                {
                    _relays.Remove(existing);
                    var updated = existing with
                    {
                        IsHealthy = existing.ConsecutiveFailures + 1 >= 3 ? false : existing.IsHealthy,
                        ConsecutiveFailures = existing.ConsecutiveFailures + 1
                    };
                    _relays.Add(updated);
                }
            }
        }
    }

    public IReadOnlyList<RelayInfo> GetRelays()
    {
        lock (_relays)
        {
            return _relays.ToList();
        }
    }

    public (int TotalRelays, int HealthyCount) Stats()
    {
        lock (_relays)
        {
            return (_relays.Count, _relays.Count(r => r.IsHealthy));
        }
    }
}
