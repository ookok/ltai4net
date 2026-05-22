using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LTAI.Infra.Network.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Network.Discovery;

public sealed class ServiceDiscovery
{
    private readonly HttpClient _http;
    private readonly ILogger<ServiceDiscovery> _logger;
    private readonly ConcurrentDictionary<string, PeerInfo> _localRegistry = new();

    public ServiceDiscovery(ILogger<ServiceDiscovery> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
    }

    public async Task<DiscoveryResponse?> QueryAsync(string discoveryEndpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync(
                $"{discoveryEndpoint.TrimEnd('/')}/api/discovery/peers",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<DiscoveryResponse>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query discovery endpoint: {Endpoint}", discoveryEndpoint);
        }

        return null;
    }

    public async Task<bool> AnnounceAsync(
        string discoveryEndpoint,
        PeerInfo peer,
        CancellationToken cancellationToken = default)
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
                $"{discoveryEndpoint.TrimEnd('/')}/api/discovery/announce",
                content, cancellationToken);

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var discoveryResponse = JsonSerializer.Deserialize<DiscoveryResponse>(responseJson);

            if (discoveryResponse?.KnownPeers != null)
            {
                foreach (var p in discoveryResponse.KnownPeers)
                    _localRegistry[p.PeerId] = p;
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to announce to discovery: {Endpoint}", discoveryEndpoint);
            return false;
        }
    }

    public IReadOnlyList<PeerInfo> GetLocalPeers() => _localRegistry.Values.ToList();
}
