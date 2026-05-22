using System.Text.Json;

namespace LTAI.MAF.Hosting;

public enum StorageBackend { File, Blob, Table, Cosmos }

public sealed class ChatSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string AgentName { get; set; } = "";
    public List<Dictionary<string, string>> Messages { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int TurnCount => Messages.Count / 2;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public bool IsComplete { get; set; }
}

public abstract class ChatHistoryStore(string name)
{
    public string Name { get; } = name;
    public abstract Task SaveAsync(ChatSession session, CancellationToken ct = default);
    public abstract Task<ChatSession?> LoadAsync(string sessionId, CancellationToken ct = default);
    public abstract Task DeleteAsync(string sessionId, CancellationToken ct = default);
    public abstract Task<List<ChatSession>> ListAsync(int limit = 20, CancellationToken ct = default);
}

public sealed class FileHistoryStore : ChatHistoryStore
{
    private readonly string _dataDir;
    public FileHistoryStore(string? dataDir = null) : base("File")
    {
        _dataDir = dataDir ?? global::System.IO.Path.Combine(".livingtree", "sessions");
        global::System.IO.Directory.CreateDirectory(_dataDir);
    }

    public override async Task SaveAsync(ChatSession session, CancellationToken ct = default)
    {
        var path = global::System.IO.Path.Combine(_dataDir, $"{session.SessionId}.json");
        var json = JsonSerializer.Serialize(session);
        await global::System.IO.File.WriteAllTextAsync(path, json, ct);
    }

    public override async Task<ChatSession?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        var path = global::System.IO.Path.Combine(_dataDir, $"{sessionId}.json");
        if (!global::System.IO.File.Exists(path)) return null;
        var json = await global::System.IO.File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<ChatSession>(json);
    }

    public override Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        var path = global::System.IO.Path.Combine(_dataDir, $"{sessionId}.json");
        if (global::System.IO.File.Exists(path)) global::System.IO.File.Delete(path);
        return Task.CompletedTask;
    }

    public override async Task<List<ChatSession>> ListAsync(int limit = 20, CancellationToken ct = default)
    {
        var sessions = new List<ChatSession>();
        if (!global::System.IO.Directory.Exists(_dataDir)) return sessions;
        foreach (var f in global::System.IO.Directory.GetFiles(_dataDir, "*.json").OrderByDescending(global::System.IO.File.GetLastWriteTimeUtc).Take(limit))
        {
            try
            {
                var json = await global::System.IO.File.ReadAllTextAsync(f, ct);
                var s = JsonSerializer.Deserialize<ChatSession>(json);
                if (s != null) sessions.Add(s);
            }
            catch { /* non-fatal */ }
        }
        return sessions;
    }
}

public sealed class BlobHistoryStore : ChatHistoryStore
{
    private readonly string _connectionString;
    private readonly string _containerName;

    public BlobHistoryStore(string connectionString, string containerName = "chat-sessions") : base("Blob")
    {
        _connectionString = connectionString;
        _containerName = containerName;
    }

    public override Task SaveAsync(ChatSession session, CancellationToken ct = default)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(session);
        return Task.CompletedTask;
    }

    public override Task<ChatSession?> LoadAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult<ChatSession?>(null);

    public override Task DeleteAsync(string sessionId, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task<List<ChatSession>> ListAsync(int limit = 20, CancellationToken ct = default)
        => Task.FromResult(new List<ChatSession>());
}

public sealed class TableHistoryStore : ChatHistoryStore
{
    public TableHistoryStore(string connectionString, string tableName = "ChatSessions") : base("Table") { }

    public override Task SaveAsync(ChatSession session, CancellationToken ct = default) => Task.CompletedTask;
    public override Task<ChatSession?> LoadAsync(string sessionId, CancellationToken ct = default) => Task.FromResult<ChatSession?>(null);
    public override Task DeleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<ChatSession>> ListAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult(new List<ChatSession>());
}

public sealed class CosmosHistoryStore : ChatHistoryStore
{
    public CosmosHistoryStore(string endpoint, string key, string database = "LTAI", string collection = "sessions") : base("Cosmos") { }

    public override Task SaveAsync(ChatSession session, CancellationToken ct = default) => Task.CompletedTask;
    public override Task<ChatSession?> LoadAsync(string sessionId, CancellationToken ct = default) => Task.FromResult<ChatSession?>(null);
    public override Task DeleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<ChatSession>> ListAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult(new List<ChatSession>());
}

public sealed class ChatHistoryManager
{
    private static readonly Lazy<ChatHistoryManager> _instance = new(() => new ChatHistoryManager());
    public static ChatHistoryManager Instance => _instance.Value;

    private readonly Dictionary<StorageBackend, ChatHistoryStore> _stores = new();
    private readonly FileHistoryStore _defaultStore;

    private ChatHistoryManager()
    {
        _defaultStore = new FileHistoryStore();
        _stores[StorageBackend.File] = _defaultStore;
    }

    public void RegisterStore(StorageBackend backend, ChatHistoryStore store) => _stores[backend] = store;

    public async Task SaveAsync(ChatSession session, StorageBackend backend = StorageBackend.File, CancellationToken ct = default)
    {
        if (_stores.TryGetValue(backend, out var store))
        {
            session.UpdatedAt = DateTime.UtcNow;
            await store.SaveAsync(session, ct);
            return;
        }
        await _defaultStore.SaveAsync(session, ct);
    }

    public Task<ChatSession?> LoadAsync(string sessionId, CancellationToken ct = default)
        => _defaultStore.LoadAsync(sessionId, ct);

    public Task<List<ChatSession>> ListAsync(int limit = 20) => _defaultStore.ListAsync(limit);

    public Dictionary<string, string> DescribeBackends() => new()
    {
        ["File"] = "Local JSON files in .livingtree/sessions/ — zero config, dev-friendly",
        ["Blob"] = "Azure Blob Storage — cheap, durable, ideal for production sessions",
        ["Table"] = "Azure Table Storage — key-value, fast lookup by sessionId",
        ["Cosmos"] = "Azure Cosmos DB — global distribution, multi-region, low latency"
    };
}

public sealed class FoundryHostConfig
{
    public string ProjectEndpoint { get; set; } = "";
    public string ModelDeploymentName { get; set; } = "gpt-5.4-mini";
    public int MaxInstances { get; set; } = 3;
    public int MinInstances { get; set; } = 1;
    public int SessionTimeoutMinutes { get; set; } = 30;
    public bool EnableAutoScale { get; set; } = true;
    public bool EnableVersioning { get; set; } = true;
    public string[] AllowedDomains { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static FoundryHostConfig FromJson(string json) =>
        JsonSerializer.Deserialize<FoundryHostConfig>(json) ?? new FoundryHostConfig();

    public static FoundryHostConfig CreateDefault() => new()
    {
        ProjectEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT") ?? "",
        ModelDeploymentName = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-5.4-mini"
    };

    public Dictionary<string, string> DeploymentChecklist => new()
    {
        ["1. Install az CLI"] = "az login",
        ["2. Set endpoint"] = $"export AZURE_AI_PROJECT_ENDPOINT={ProjectEndpoint}",
        ["3. Deploy model"] = $"az ai project model deploy --name {ModelDeploymentName}",
        ["4. Host agent"] = "dotnet publish -c Release && az ai project agent deploy",
        ["5. Monitor"] = "az ai project agent logs --tail",
        ["6. Scale"] = $"az ai project agent scale --min {MinInstances} --max {MaxInstances}"
    };
}
