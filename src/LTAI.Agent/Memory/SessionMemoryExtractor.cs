using LTAI.Agent.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed class SessionMemoryExtractor : BackgroundService
{
    private readonly PalaceStore _store;
    private readonly TaskQueue _taskQueue;
    private readonly ILogger<SessionMemoryExtractor> _logger;

    public SessionMemoryExtractor(PalaceStore store, TaskQueue taskQueue,
        ILogger<SessionMemoryExtractor>? logger = null)
    {
        _store = store;
        _taskQueue = taskQueue;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionMemoryExtractor>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionMemoryExtractor: started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested) break;
                await ExtractFromRecentSessionsAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionMemoryExtractor: cycle failed");
            }
        }
    }

    public async Task ExtractFromRecentSessionsAsync(int recentMinutes = 30)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (recentMinutes * 60 * 1000);

        var recent = new List<PalaceStore.Drawer>();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM palace
            WHERE (expires_at IS NULL OR expires_at > @now)
              AND created_at >= @cutoff
              AND wing = 'diary'
            ORDER BY created_at ASC
            LIMIT 100
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rdr.ReadAsync().ConfigureAwait(false))
            recent.Add(PalaceStore.ReadDrawer(rdr));

        if (recent.Count < 3) return;

        var wing = "session-extract";
        var room = $"auto-{DateTime.UtcNow:yyyyMMdd}";

        var allContent = string.Join("\n", recent.Select(d => d.Content));
        if (allContent.Length > 4000) allContent = allContent[..4000];

        var alreadyExtracted = await CheckExistingExtractAsync(wing, room, cutoff).ConfigureAwait(false);
        if (alreadyExtracted) return;

        var extractId = await _store.StoreAsync(
            wing: wing, room: room,
            content: allContent,
            role: "consolidation",
            importance: 0.5,
            agentId: "session-extractor",
            ttlMs: 30L * 24 * 60 * 60 * 1000).ConfigureAwait(false);

        _logger.LogInformation("SessionMemoryExtractor: extracted {Count} entries → {Wing}/{Room} (id={Id})",
            recent.Count, wing, room, extractId);
    }

    private async Task<bool> CheckExistingExtractAsync(string wing, string room, long cutoff)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_store.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM palace WHERE wing=@w AND room=@r AND created_at>=@cutoff";
        cmd.Parameters.AddWithValue("@w", wing);
        cmd.Parameters.AddWithValue("@r", room);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        return count > 0;
    }
}
