using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Network.Consensus;

public enum NetworkQualityLevel
{
    Excellent,
    Good,
    Fair,
    Poor,
    Dead
}

public sealed record NetworkQuality
{
    [JsonPropertyName("packet_loss_pct")]
    public double PacketLossPct { get; init; }

    [JsonPropertyName("avg_latency_ms")]
    public double AvgLatencyMs { get; init; }

    [JsonPropertyName("jitter_ms")]
    public double JitterMs { get; init; }

    [JsonPropertyName("bandwidth_kbps")]
    public double BandwidthKbps { get; init; }

    [JsonPropertyName("quality_level")]
    public NetworkQualityLevel QualityLevel { get; init; }

    [JsonPropertyName("degraded")]
    public bool Degraded { get; init; }

    [JsonPropertyName("degrade_reason")]
    public string? DegradeReason { get; init; }
}

public sealed class NetworkQualityMonitor
{
    private readonly Queue<(DateTime Timestamp, double LatencyMs, bool Success)> _window = new();
    private readonly List<NetworkQuality> _history = new();
    private readonly object _historyLock = new();
    private readonly int _windowSize;

    public NetworkQualityMonitor(int windowSize = 10)
    {
        _windowSize = windowSize;
    }

    public void Record(double latencyMs, bool success)
    {
        _window.Enqueue((DateTime.UtcNow, latencyMs, success));

        while (_window.Count > _windowSize)
            _window.TryDequeue(out _);
    }

    public NetworkQuality Quality()
    {
        var entries = _window.ToArray();

        if (entries.Length == 0)
        {
            return new NetworkQuality
            {
                QualityLevel = NetworkQualityLevel.Dead,
                Degraded = true,
                DegradeReason = "No data available"
            };
        }

        var total = entries.Length;
        var failed = entries.Count(e => !e.Success);
        var packetLossPct = (double)failed / total * 100.0;
        var avgLatency = entries.Average(e => e.LatencyMs);

        var jitter = 0.0;
        if (entries.Length > 1)
        {
            var latencies = entries.Select(e => e.LatencyMs).ToArray();
            double sumSquaredDiff = 0;
            for (int i = 1; i < latencies.Length; i++)
            {
                var diff = latencies[i] - latencies[i - 1];
                sumSquaredDiff += diff * diff;
            }
            jitter = Math.Sqrt(sumSquaredDiff / (latencies.Length - 1));
        }

        var bandwidthKbps = entries.Length > 0 ? 1024.0 / Math.Max(avgLatency / 1000.0, 0.001) : 0.0;

        var level = DetermineLevel(packetLossPct, avgLatency);

        var quality = new NetworkQuality
        {
            PacketLossPct = Math.Round(packetLossPct, 2),
            AvgLatencyMs = Math.Round(avgLatency, 2),
            JitterMs = Math.Round(jitter, 2),
            BandwidthKbps = Math.Round(bandwidthKbps, 2),
            QualityLevel = level,
            Degraded = level is NetworkQualityLevel.Poor or NetworkQualityLevel.Dead,
            DegradeReason = level switch
            {
                NetworkQualityLevel.Dead => "Network is dead or no data",
                NetworkQualityLevel.Poor => "High latency or packet loss",
                _ => null
            }
        };

        lock (_historyLock)
        {
            _history.Add(quality);
        }

        return quality;
    }

    public bool ShouldDegrade()
    {
        var quality = Quality();
        return quality.Degraded || quality.AvgLatencyMs > 2000;
    }

    public string AutoStrategy()
    {
        var quality = Quality();
        return quality.QualityLevel switch
        {
            NetworkQualityLevel.Excellent or NetworkQualityLevel.Good => "protobuf",
            NetworkQualityLevel.Fair => "json",
            NetworkQualityLevel.Poor or NetworkQualityLevel.Dead => "offline",
            _ => "json"
        };
    }

    public Dictionary<string, int> Stats()
    {
        lock (_historyLock)
        {
            return _history
                .GroupBy(q => q.QualityLevel)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());
        }
    }

    private static NetworkQualityLevel DetermineLevel(double packetLossPct, double avgLatency)
    {
        if (packetLossPct > 50 || avgLatency > 3000)
            return NetworkQualityLevel.Dead;
        if (packetLossPct > 20 || avgLatency > 1000)
            return NetworkQualityLevel.Poor;
        if (packetLossPct > 5 || avgLatency > 300)
            return NetworkQualityLevel.Fair;
        if (packetLossPct > 1 || avgLatency > 100)
            return NetworkQualityLevel.Good;
        return NetworkQualityLevel.Excellent;
    }
}

public sealed record SwarmTask
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("source_node")]
    public string SourceNode { get; init; } = string.Empty;

    [JsonPropertyName("target_nodes")]
    public List<string> TargetNodes { get; init; } = new();

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class SwarmCoordinator
{
    private static readonly Lazy<SwarmCoordinator> _instance = new(() => new SwarmCoordinator());
    public static SwarmCoordinator Instance => _instance.Value;

    private readonly NetworkQualityMonitor _monitor = new();
    private readonly ConcurrentDictionary<string, SwarmTask> _tasks = new();
    private readonly ILogger<SwarmCoordinator> _logger;

    private SwarmCoordinator()
    {
        _logger = NullLogger<SwarmCoordinator>.Instance;
    }

    public async Task DistributeTaskAsync(
        string taskDescription,
        List<string> targetNodes,
        DistributedConsciousness? consciousness,
        CancellationToken cancellationToken = default)
    {
        var strategy = _monitor.AutoStrategy();
        if (strategy == "offline")
        {
            _logger.LogWarning("Network degraded, deferring task distribution");
            return;
        }

        var taskId = Guid.NewGuid().ToString("N");
        var task = new SwarmTask
        {
            TaskId = taskId,
            Description = taskDescription,
            SourceNode = consciousness?.Stats().GetValueOrDefault("instance_id", "unknown")?.ToString() ?? "unknown",
            TargetNodes = targetNodes,
            Status = "distributed"
        };

        _tasks.TryAdd(taskId, task);

        _logger.LogInformation(
            "Task {TaskId} distributed to {NodeCount} nodes via {Strategy}: {Description}",
            taskId, targetNodes.Count, strategy, taskDescription);

        await Task.CompletedTask;
    }

    public void ReceiveTask(SwarmTask task)
    {
        if (_tasks.TryAdd(task.TaskId, task))
        {
            _logger.LogInformation("Received task {TaskId} from {Source}: {Description}",
                task.TaskId, task.SourceNode, task.Description);
        }
    }

    public List<SwarmTask> GetTasks(string nodeId)
    {
        return _tasks.Values
            .Where(t => t.TargetNodes.Contains(nodeId) || t.SourceNode == nodeId)
            .ToList();
    }

    public double Goodput
    {
        get
        {
            var quality = _monitor.Quality();
            if (quality.AvgLatencyMs <= 0)
                return 0.0;

            return quality.BandwidthKbps * (1.0 - quality.PacketLossPct / 100.0);
        }
    }

    public List<string> GetTrustedPeers(double minScore = 0.5)
    {
        _logger.LogInformation("GetTrustedPeers with minScore={MinScore} - placeholder implementation", minScore);
        return new List<string>();
    }

    public Dictionary<string, object> Stats()
    {
        var quality = _monitor.Quality();
        var monitorStats = _monitor.Stats();

        return new Dictionary<string, object>
        {
            ["task_count"] = _tasks.Count,
            ["quality_level"] = quality.QualityLevel.ToString(),
            ["avg_latency_ms"] = quality.AvgLatencyMs,
            ["packet_loss_pct"] = quality.PacketLossPct,
            ["jitter_ms"] = quality.JitterMs,
            ["bandwidth_kbps"] = quality.BandwidthKbps,
            ["degraded"] = quality.Degraded,
            ["strategy"] = _monitor.AutoStrategy(),
            ["goodput"] = Goodput,
            ["quality_history"] = monitorStats
        };
    }
}
