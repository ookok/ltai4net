using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LTAI.Core.Configuration;

namespace LTAI.Core.Storage;

public sealed class UnifiedDbManager : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _dbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<UnifiedDbManager> _logger;
    private bool _disposed;

    public UnifiedDbManager(ILogger<UnifiedDbManager>? logger = null)
    {
        _logger = logger ?? NullLogger<UnifiedDbManager>.Instance;
    }

    public void Register(string name, string dbPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        var fullPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _dbs[name] = fullPath;
        _logger.LogDebug("UnifiedDb: registered '{Name}' -> {Path}", name, fullPath);
    }

    public string GetPath(string name)
    {
        if (_dbs.TryGetValue(name, out var path))
            return path;
        throw new KeyNotFoundException($"Database '{name}' not registered. Available: {string.Join(", ", _dbs.Keys)}");
    }

    public string GetConnectionString(string name, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate, bool pooling = true)
    {
        var path = GetPath(name);
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Shared,
            Pooling = pooling,
        }.ToString();
    }

    public SqliteConnection OpenConnection(string name, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var conn = new SqliteConnection(GetConnectionString(name, mode));
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    public (SqliteConnection Writer, SqliteConnection Reader) OpenDualConnection(string name)
    {
        var writer = OpenConnection(name, SqliteOpenMode.ReadWriteCreate);
        var reader = OpenConnection(name, SqliteOpenMode.ReadWrite);
        return (writer, reader);
    }

    public IReadOnlyDictionary<string, string> GetAll()
    {
        var snapshot = new Dictionary<string, string>(_dbs, StringComparer.OrdinalIgnoreCase);
        return snapshot;
    }

    public IEnumerable<string> GetAllPaths() => _dbs.Values.Distinct(StringComparer.OrdinalIgnoreCase);

    private static void ApplyPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout={EnvironmentConfig.SqliteBusyMs};
            PRAGMA temp_store=MEMORY;
        """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
