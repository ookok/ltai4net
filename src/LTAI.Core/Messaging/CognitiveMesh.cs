using System.Collections.Concurrent;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Messaging;

public sealed class CognitiveMesh : ICognitiveMesh
{
    private readonly ConcurrentDictionary<string, ILayerGovernor> _governors = new();
    private readonly ConcurrentDictionary<string, Handshake> _pending = new();
    private readonly ConcurrentDictionary<string, Handshake> _worldState = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<Handshake>> _handshakeLog = new();
    private readonly ConcurrentDictionary<string, LayerStats> _stats = new();
    private readonly ILogger<CognitiveMesh> _logger;
    private readonly object _lock = new();

    public CognitiveMesh(ILogger<CognitiveMesh> logger)
    {
        _logger = logger;
    }

    public Task RegisterAsync(ILayerGovernor governor, CancellationToken cancellationToken = default)
    {
        _governors[governor.LayerName] = governor;
        _stats.TryAdd(governor.LayerName, new LayerStats { LayerName = governor.LayerName });
        _logger.LogInformation("Registered governor: {Layer}", governor.LayerName);
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string layerName, CancellationToken cancellationToken = default)
    {
        _governors.TryRemove(layerName, out _);
        _logger.LogInformation("Unregistered governor: {Layer}", layerName);
        return Task.CompletedTask;
    }

    public async Task<Handshake> SendAsync(Handshake handshake, CancellationToken cancellationToken = default)
    {
        var log = _handshakeLog.GetOrAdd(handshake.To, _ => new ConcurrentBag<Handshake>());
        log.Add(handshake);

        if (!string.IsNullOrEmpty(handshake.ReplyTo))
        {
            _pending[handshake.ReplyTo] = handshake;
        }

        if (_governors.TryGetValue(handshake.To, out var governor))
        {
            try
            {
                _stats[handshake.To].MessagesReceived++;
                var sw = global::System.Diagnostics.Stopwatch.StartNew();
                var response = await governor.ProcessAsync(handshake, cancellationToken);
                sw.Stop();
                _stats[handshake.To].AvgLatencyMs =
                    (_stats[handshake.To].AvgLatencyMs * (_stats[handshake.To].MessagesReceived - 1) + sw.Elapsed.TotalMilliseconds)
                    / _stats[handshake.To].MessagesReceived;
                _stats[handshake.To].LastActive = DateTime.UtcNow;

                if (response.Payload != null)
                {
                    _pending.TryRemove(response.ReplyTo ?? "", out _);
                }

                return response;
            }
            catch (Exception ex)
            {
                _stats[handshake.To].Errors++;
                _logger.LogError(ex, "Error processing handshake {Action} for {Layer}", handshake.Action, handshake.To);
                return new Handshake { From = handshake.To, Action = "error", Payload = new Dictionary<string, object?> { ["error"] = ex.Message } };
            }
        }

        _logger.LogWarning("No governor found for layer: {Layer}", handshake.To);
        return new Handshake { From = "mesh", Action = "no_route", Payload = new Dictionary<string, object?> { ["target"] = handshake.To } };
    }

    public Task BroadcastAsync(Handshake handshake, CancellationToken cancellationToken = default)
    {
        var tasks = _governors.Values.Select(g => g.ProcessAsync(handshake, cancellationToken));
        return Task.WhenAll(tasks);
    }

    public Handshake? GetWorldState(string key)
    {
        lock (_lock)
        {
            return _worldState.TryGetValue(key, out var state) ? state : null;
        }
    }

    public void SetWorldState(string key, Handshake state)
    {
        lock (_lock)
        {
            _worldState[key] = state;
        }
    }

    public bool HasPending(string replyTo)
    {
        return _pending.ContainsKey(replyTo);
    }
}
