using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class StorageGovernor : LayerGovernor
{
    private readonly Dictionary<string, object> _cache = new();

    public StorageGovernor(ICognitiveMesh mesh, IProviderEngine llm, ILogger<StorageGovernor> logger)
        : base("storage", mesh, llm, logger) { }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var action = incoming.Action;

        return action switch
        {
            "cache_put" => HandleCachePut(incoming),
            "cache_get" => HandleCacheGet(incoming),
            "cache_clear" => HandleCacheClear(),
            _ => Task.FromResult(new Handshake { From = LayerName, Action = "storage_ack" })
        };
    }

    private Task<Handshake> HandleCachePut(Handshake incoming)
    {
        var key = incoming.Payload?.GetValueOrDefault("key")?.ToString() ?? "";
        var value = incoming.Payload?.GetValueOrDefault("value");
        if (!string.IsNullOrEmpty(key) && value != null)
        {
            _cache[key] = value;
            Logger.LogInformation("Cache put: {Key}", key);
        }
        return Task.FromResult(new Handshake { From = LayerName, Action = "cached" });
    }

    private Task<Handshake> HandleCacheGet(Handshake incoming)
    {
        var key = incoming.Payload?.GetValueOrDefault("key")?.ToString() ?? "";
        _cache.TryGetValue(key, out var value);
        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "cache_hit",
            Payload = new Dictionary<string, object?> { ["key"] = key, ["value"] = value, ["hit"] = value != null }
        });
    }

    private Task<Handshake> HandleCacheClear()
    {
        _cache.Clear();
        Logger.LogInformation("Cache cleared");
        return Task.FromResult(new Handshake { From = LayerName, Action = "cache_cleared" });
    }
}
