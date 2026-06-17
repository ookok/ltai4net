using LTAI.Agent.Vector;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed class PalaceFeedbackTracker
{
    private readonly PalaceStore _store;
    private readonly ILogger<PalaceFeedbackTracker>? _logger;

    public PalaceFeedbackTracker(PalaceStore store, ILogger<PalaceFeedbackTracker>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public async Task RecordInteractionAsync(string drawerId)
    {
        using var conn = new SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE palace SET
                access_count = access_count + 1,
                last_accessed_at = @now,
                importance = MIN(0.95, importance + 0.02)
            WHERE drawer_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", drawerId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task RecordContradictionAsync(string drawerId)
    {
        using var conn = new SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE palace SET
                access_count = access_count + 1,
                last_accessed_at = @now,
                importance = MAX(0.05, importance - 0.15)
            WHERE drawer_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", drawerId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        _logger?.LogInformation("Feedback: contradiction recorded for {DrawerId}", drawerId);
    }

    public async Task RecordConfidenceAsync(string drawerId, double delta)
    {
        var clamped = Math.Clamp(delta, -0.3, 0.3);
        using var conn = new SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE palace SET
                access_count = access_count + 1,
                last_accessed_at = @now,
                importance = MAX(0.05, MIN(0.95, importance + @delta))
            WHERE drawer_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", drawerId);
        cmd.Parameters.AddWithValue("@delta", clamped);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string DrawerId, double Importance, int AccessCount)>> GetLowConfidenceAsync(double threshold = 0.2, int limit = 50)
    {
        var results = new List<(string, double, int)>();
        using var conn = new SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT drawer_id, importance, access_count
            FROM palace
            WHERE importance < @threshold AND (expires_at IS NULL OR expires_at > @now)
            ORDER BY importance ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@threshold", threshold);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rdr.ReadAsync().ConfigureAwait(false))
            results.Add((rdr.GetString(0), rdr.GetDouble(1), rdr.GetInt32(2)));
        return results;
    }
}
