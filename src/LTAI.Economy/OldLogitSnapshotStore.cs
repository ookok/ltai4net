using System.Collections.Concurrent;
using System.Text.Json;

namespace LTAI.Economy;

public sealed record OldLogitSnapshot
{
    public int Version { get; set; }
    public Dictionary<string, double> ToolPreferences { get; set; } = new();
    public double Timestamp { get; set; }
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public List<string> AssociatedTrajectoryIds { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

public sealed class OldLogitSnapshotStore
{
    private readonly ConcurrentDictionary<int, OldLogitSnapshot> _snapshots = new();
    private readonly ConcurrentQueue<OldLogitSnapshot> _rollbackQueue = new();
    private int _currentVersion;
    private const int MaxSnapshots = 50;
    private const int MaxRollbackHistory = 20;
    private readonly string _persistDir;
    private readonly object _lock = new();

    public OldLogitSnapshotStore()
    {
        _persistDir = global::System.IO.Path.Combine(".livingtree", "old_logit_snapshots");
        global::System.IO.Directory.CreateDirectory(_persistDir);
        Load();
    }

    public int CurrentVersion => _currentVersion;

    public OldLogitSnapshot SaveSnapshot(Dictionary<string, double> preferences)
    {
        var version = Interlocked.Increment(ref _currentVersion);

        var snapshot = new OldLogitSnapshot
        {
            Version = version,
            ToolPreferences = new Dictionary<string, double>(preferences),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        _snapshots[version] = snapshot;
        _rollbackQueue.Enqueue(snapshot);

        lock (_lock)
        {
            while (_rollbackQueue.Count > MaxRollbackHistory)
                _rollbackQueue.TryDequeue(out _);

            if (_snapshots.Count > MaxSnapshots)
            {
                var oldestKey = _snapshots.Keys.Min();
                _snapshots.TryRemove(oldestKey, out _);
            }
        }

        Persist();
        return snapshot;
    }

    public OldLogitSnapshot? GetSnapshot(int version)
    {
        _snapshots.TryGetValue(version, out var snapshot);
        return snapshot;
    }

    public OldLogitSnapshot? GetSnapshotForTrajectory(string trajectoryId)
    {
        return _snapshots.Values
            .FirstOrDefault(s => s.AssociatedTrajectoryIds.Contains(trajectoryId));
    }

    public void AssociateTrajectory(int version, string trajectoryId)
    {
        if (_snapshots.TryGetValue(version, out var snapshot))
        {
            lock (snapshot)
            {
                if (!snapshot.AssociatedTrajectoryIds.Contains(trajectoryId))
                    snapshot.AssociatedTrajectoryIds.Add(trajectoryId);
            }
        }
    }

    public bool HasExactOldLogits(int version) => _snapshots.ContainsKey(version);

    public Dictionary<string, double>? GetOldPreferences(int version)
    {
        if (_snapshots.TryGetValue(version, out var snapshot) && snapshot.IsActive)
            return new Dictionary<string, double>(snapshot.ToolPreferences);

        return null;
    }

    public double ComputeDiscrepancyRatio(
        Dictionary<string, double> trainingPrefs,
        Dictionary<string, double> inferencePrefs,
        string toolName)
    {
        var trainProb = trainingPrefs.TryGetValue(toolName, out var tp) ? tp : 1e-6;
        var inferProb = inferencePrefs.TryGetValue(toolName, out var ip) ? ip : 1e-6;

        inferProb = Math.Max(1e-9, inferProb);
        trainProb = Math.Max(1e-9, trainProb);

        return trainProb / inferProb;
    }

    public double ComputeStalenessRatio(
        Dictionary<string, double> currentPrefs,
        Dictionary<string, double> oldPrefs,
        string toolName)
    {
        var curProb = currentPrefs.TryGetValue(toolName, out var cp) ? cp : 1e-6;
        var oldProb = oldPrefs.TryGetValue(toolName, out var op) ? op : 1e-6;

        oldProb = Math.Max(1e-9, oldProb);
        curProb = Math.Max(1e-9, curProb);

        return curProb / oldProb;
    }

    public bool Rollback(int targetVersion)
    {
        var queueList = _rollbackQueue.ToList();
        for (int i = queueList.Count - 1; i >= 0; i--)
        {
            if (queueList[i].Version == targetVersion)
            {
                _currentVersion = targetVersion;
                return true;
            }
        }
        return false;
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["current_version"] = _currentVersion,
                ["snapshot_count"] = _snapshots.Count,
                ["rollback_history"] = _rollbackQueue.Count,
                ["versions"] = _snapshots.Keys.OrderBy(k => k).ToList(),
                ["active_snapshots"] = _snapshots.Values.Count(s => s.IsActive)
            };
        }
    }

    public void DeactivateOldSnapshots(int keepVersions)
    {
        var toDeactivate = _snapshots.Values
            .OrderByDescending(s => s.Version)
            .Skip(keepVersions)
            .ToList();

        foreach (var s in toDeactivate)
            s.IsActive = false;
    }

    private void Persist()
    {
        try
        {
            var path = global::System.IO.Path.Combine(_persistDir, "snapshots.json");
            var data = _snapshots.Values.Select(s => new
            {
                s.Version, s.ToolPreferences, s.Timestamp,
                s.SnapshotId, s.AssociatedTrajectoryIds, s.IsActive
            }).ToList();

            global::System.IO.File.WriteAllText(path, JsonSerializer.Serialize(data));
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            var path = global::System.IO.Path.Combine(_persistDir, "snapshots.json");
            if (!global::System.IO.File.Exists(path)) return;

            var json = global::System.IO.File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (data == null) return;

            foreach (var item in data)
            {
                var snapshot = new OldLogitSnapshot
                {
                    Version = item.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    Timestamp = item.TryGetProperty("timestamp", out var t) ? t.GetDouble() : 0,
                    SnapshotId = item.TryGetProperty("snapshotId", out var sid) ? sid.GetString() ?? "" : "",
                    IsActive = item.TryGetProperty("isActive", out var ia) ? ia.GetBoolean() : true
                };

                if (item.TryGetProperty("toolPreferences", out var tp))
                {
                    var prefDict = JsonSerializer.Deserialize<Dictionary<string, double>>(tp.GetRawText());
                    if (prefDict != null) snapshot.ToolPreferences = prefDict;
                }

                if (item.TryGetProperty("associatedTrajectoryIds", out var ati))
                {
                    var ids = JsonSerializer.Deserialize<List<string>>(ati.GetRawText());
                    if (ids != null) snapshot.AssociatedTrajectoryIds = ids;
                }

                _snapshots[snapshot.Version] = snapshot;
                _rollbackQueue.Enqueue(snapshot);
                if (snapshot.Version > _currentVersion)
                    _currentVersion = snapshot.Version;
            }
        }
        catch { }
    }
}
