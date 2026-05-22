using System.Text.Json;
using LiteDB;
using LTAI.Infra.Network.Models;
using Microsoft.Extensions.Logging;
using STJ = System.Text.Json.JsonSerializer;

namespace LTAI.Infra.Network.Messaging;

public sealed class PersistentMessageQueue : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<QueueEntry> _collection;
    private readonly ILogger<PersistentMessageQueue> _logger;
    private readonly string _dbPath;
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) { return _collection.Count(); } }
    }

    public PersistentMessageQueue(string dbPath, ILogger<PersistentMessageQueue> logger)
    {
        _dbPath = dbPath;
        _logger = logger;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase(dbPath);
        _collection = _db.GetCollection<QueueEntry>("p2p_messages");
        _collection.EnsureIndex(x => x.EnqueueTime);

        var recovered = _collection.Count();
        if (recovered > 0)
            _logger.LogInformation("PersistentMessageQueue recovered {Count} messages from {Path}", recovered, dbPath);
    }

    public void Enqueue(NetworkMessage message)
    {
        var json = STJ.Serialize(message, JsonOpts);
        var entry = new QueueEntry
        {
            Payload = json,
            EnqueueTime = DateTime.UtcNow
        };

        lock (_lock) { _collection.Insert(entry); }
        _logger.LogDebug("Enqueued message: {Action} (queue size: {Count})", message.Action, Count);
    }

    public NetworkMessage? Dequeue()
    {
        lock (_lock)
        {
            var entry = _collection.Query()
                .OrderBy(x => x.EnqueueTime)
                .Limit(1)
                .FirstOrDefault();

            if (entry == null) return null;

            try
            {
                var message = STJ.Deserialize<NetworkMessage>(entry.Payload, JsonOpts);
                _collection.Delete(entry.Id);
                return message;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize message, discarding: {Id}", entry.Id);
                _collection.Delete(entry.Id);
                return null;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _collection.DeleteAll();
        }
        _logger.LogInformation("PersistentMessageQueue cleared: {Path}", _dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}

internal sealed class QueueEntry
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public string Payload { get; set; } = string.Empty;
    public DateTime EnqueueTime { get; set; } = DateTime.UtcNow;
}
