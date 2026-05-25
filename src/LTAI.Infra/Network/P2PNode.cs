using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Infra.Network.Interfaces;
using LTAI.Infra.Network.Messaging;
using LTAI.Infra.Network.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Infra.Network;

public sealed class P2PNode : IP2PNode, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<P2PNode> _logger;
    private readonly PersistentMessageQueue _persistentQueue;
    private readonly ConcurrentDictionary<string, PeerInfo> _knownPeers = new();
    private readonly SemaphoreSlim _requestSemaphore = new(20);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public string PeerId { get; }
    public int LocalPort => _options.Value.Network.P2PPort;

    public P2PNode(
        IHttpClientFactory httpClientFactory,
        IOptions<LTAIOptions> options,
        ILogger<P2PNode> logger,
        PersistentMessageQueue persistentQueue)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _persistentQueue = persistentQueue;
        PeerId = GeneratePeerId();
    }

    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient("p2p");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var port = _options.Value.Network.P2PPort;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://0.0.0.0:{port}/");
            _listener.Start();
            _listenerTask = Task.Run(() => ListenLoop(_cts.Token), _cts.Token);
            _logger.LogInformation("P2P Node started: {PeerId} on port {Port}", PeerId, port);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogWarning(ex, "Failed to bind P2P listener on port {Port}. Running in outbound-only mode.", port);
        }

        await AnnounceSelfAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();

        if (_listenerTask != null)
        {
            try { await _listenerTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch { /* timeout is acceptable during shutdown */ }
        }

        _listener?.Stop();
        _listener?.Close();
        _logger.LogInformation("P2P Node stopped: {PeerId}", PeerId);
    }

    public async Task SendMessageAsync(NetworkMessage message, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Sending message: {Action} -> {ToPeer}", message.Action, message.ToPeer ?? "broadcast");

        var json = JsonSerializer.Serialize(message, JsonOpts);

        if (!string.IsNullOrEmpty(message.ToPeer) && _knownPeers.TryGetValue(message.ToPeer, out var target))
        {
            await SendToPeerAsync(target, json, message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var tasks = _knownPeers.Values
                .Where(p => p.IsActive && p.PeerId != PeerId)
                .Select(p => SendToPeerAsync(p, json, message, cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task SendToPeerAsync(PeerInfo peer, string json, NetworkMessage message, CancellationToken ct)
    {
        using var client = CreateHttpClient();
        try
        {
            var url = $"http://{peer.Address}:{peer.Port}/api/p2p/messages";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                _logger.LogDebug("Message delivered to {PeerId} at {Address}:{Port}", peer.PeerId, peer.Address, peer.Port);
            else
                _logger.LogWarning("Failed to deliver message to {PeerId}: {Status}", peer.PeerId, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to {PeerId} at {Address}:{Port}", peer.PeerId, peer.Address, peer.Port);
            var updated = new PeerInfo
            {
                PeerId = peer.PeerId,
                Address = peer.Address,
                Port = peer.Port,
                IsActive = false,
                LastSeen = peer.LastSeen,
                Metadata = peer.Metadata
            };
            _knownPeers.TryUpdate(peer.PeerId, updated, peer);
        }
    }

    public Task<IReadOnlyList<PeerInfo>> GetKnownPeersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<PeerInfo>>(_knownPeers.Values.ToList());
    }

    public async IAsyncEnumerable<NetworkMessage> ReceiveMessagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = _persistentQueue.Dequeue();
            if (message != null)
            {
                yield return message;
            }
            else
            {
                try { await Task.Delay(50, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task RegisterPeerAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        _knownPeers[peer.PeerId] = peer;
        _logger.LogInformation("Peer registered: {PeerId} at {Address}:{Port}", peer.PeerId, peer.Address, peer.Port);

        if (!string.IsNullOrEmpty(_options.Value.Network.DiscoveryEndpoint))
        {
            await AnnounceToDiscoveryAsync(peer, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AnnounceSelfAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.Value.Network.DiscoveryEndpoint))
            return;

        var self = new PeerInfo
        {
            PeerId = PeerId,
            Address = "localhost",
            Port = LocalPort,
            IsActive = true,
            Metadata = new Dictionary<string, string> { ["role"] = "ltai-node" }
        };

        await AnnounceToDiscoveryAsync(self, ct).ConfigureAwait(false);
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

            using var client = CreateHttpClient();
            var response = await client.PostAsync(
                $"{_options.Value.Network.DiscoveryEndpoint.TrimEnd('/')}/api/discovery/announce",
                content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var discoveryResponse = JsonSerializer.Deserialize<DiscoveryResponse>(responseJson, JsonOpts);
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

    private async Task ListenLoop(CancellationToken ct)
    {
        if (_listener == null) return;

        var activeTasks = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                await _requestSemaphore.WaitAsync(ct).ConfigureAwait(false);
                var task = Task.Run(async () =>
                {
                    try { await HandleRequest(context, ct); }
                    finally { _requestSemaphore.Release(); }
                }, ct);
                activeTasks.Add(task);
                activeTasks.RemoveAll(t => t.IsCompleted);
            }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
        }

        if (activeTasks.Count > 0)
        {
            try { await Task.WhenAll(activeTasks).WaitAsync(TimeSpan.FromSeconds(10), ct); } catch { }
        }
    }

    private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/api/p2p/messages")
            {
                using var reader = new StreamReader(request.InputStream);
                var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize<NetworkMessage>(body, JsonOpts);

                if (message != null)
                {
                    _persistentQueue.Enqueue(message);
                    _knownPeers.AddOrUpdate(message.FromPeer,
                        _ => new PeerInfo { PeerId = message.FromPeer, Address = request.RemoteEndPoint.Address.ToString(), Port = request.RemoteEndPoint.Port, IsActive = true, LastSeen = DateTime.UtcNow },
                        (_, existing) => new PeerInfo { PeerId = existing.PeerId, Address = existing.Address, Port = existing.Port, IsActive = true, LastSeen = DateTime.UtcNow, Metadata = existing.Metadata });

                    response.StatusCode = 200;
                    var ack = JsonSerializer.Serialize(new { status = "ack", message_id = message.MessageId }, JsonOpts);
                    var ackBytes = Encoding.UTF8.GetBytes(ack);
                    response.ContentLength64 = ackBytes.Length;
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(ackBytes, ct).ConfigureAwait(false);
                }
                else
                {
                    response.StatusCode = 400;
                }
            }
            else if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/api/p2p/peers")
            {
                var peers = _knownPeers.Values.ToList();
                var json = JsonSerializer.Serialize(peers, JsonOpts);
                var bytes = Encoding.UTF8.GetBytes(json);
                response.ContentLength64 = bytes.Length;
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                response.StatusCode = 200;
            }
            else if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/api/p2p/health")
            {
                var health = new { peer_id = PeerId, port = LocalPort, peer_count = _knownPeers.Count, status = "alive" };
                var json = JsonSerializer.Serialize(health, JsonOpts);
                var bytes = Encoding.UTF8.GetBytes(json);
                response.ContentLength64 = bytes.Length;
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                response.StatusCode = 200;
            }
            else
            {
                response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling P2P request");
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }

    private static string GeneratePeerId()
    {
        Span<byte> bytes = stackalloc byte[8];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _requestSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
