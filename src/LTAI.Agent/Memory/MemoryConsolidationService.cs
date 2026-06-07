using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

/// <summary>
/// Background service that periodically consolidates, decays, and prunes
/// the PalaceStore — analogous to sleep-dependent memory consolidation.
/// Runs every 30 minutes.
/// </summary>
public sealed class MemoryConsolidationService : BackgroundService
{
    private readonly PalaceStore _store;
    private readonly ILogger<MemoryConsolidationService>? _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    public MemoryConsolidationService(PalaceStore store, ILogger<MemoryConsolidationService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("MemoryConsolidationService: started, interval={Interval}", Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested) break;
                ConsolidateOnce();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MemoryConsolidationService: cycle failed");
            }
        }
    }

    private void ConsolidateOnce()
    {
        // 1. Prune expired
        var expired = _store.CleanupExpired();

        // 2. Decay importance ×0.95
        var decayed = _store.DecayAll(factor: 0.95, minImportance: 0.05);

        // 3. Consolidate rooms with multiple drawers
        var rooms = _store.ListRooms();
        var merged = 0;
        foreach (var wing in _store.ListWings())
        {
            var wingRooms = _store.ListRooms(wing);
            foreach (var room in wingRooms)
                merged += _store.ConsolidateRoom(wing, room);
        }

        if (expired > 0 || decayed > 0 || merged > 0)
            _logger?.LogInformation(
                "MemoryConsolidationService: expired={Expired} decayed={Decayed} merged={Merged} total_rooms={Rooms}",
                expired, decayed, merged, rooms.Count);
    }
}
