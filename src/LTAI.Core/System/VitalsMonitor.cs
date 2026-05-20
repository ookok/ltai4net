using System.Diagnostics;

namespace LTAI.Core.System;

public sealed class VitalsMonitor
{
    private static readonly Lazy<VitalsMonitor> _instance = new(() => new VitalsMonitor());
    public static VitalsMonitor Instance => _instance.Value;

    private readonly DateTime _startTime = DateTime.UtcNow;
    private int _dpoPositive;
    private int _dpoNegative;
    private Dictionary<string, object>? _lastVitals;

    private VitalsMonitor() { }

    public void RecordFeedback(bool positive)
    {
        if (positive)
            Interlocked.Increment(ref _dpoPositive);
        else
            Interlocked.Increment(ref _dpoNegative);
    }

    public Dictionary<string, object> Measure()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var process = Process.GetCurrentProcess();

        double cpuPercent;
        try
        {
            var startTime = process.TotalProcessorTime;
            var startWall = DateTime.UtcNow;
            Thread.Sleep(500);
            var endTime = process.TotalProcessorTime;
            var endWall = DateTime.UtcNow;
            cpuPercent = (endTime - startTime).TotalMilliseconds /
                         (endWall - startWall).TotalMilliseconds /
                         Environment.ProcessorCount * 100;
        }
        catch
        {
            cpuPercent = 50;
        }

        var memBytes = process.WorkingSet64;
        var memMb = memBytes / (1024.0 * 1024.0);
        var totalMemMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
        var memPercent = totalMemMb > 0 ? memMb / totalMemMb * 100 : 50;

        var cpuLevel = cpuPercent switch
        {
            > 80 => "breathing_fast",
            > 50 => "breathing_normal",
            > 20 => "breathing_slow",
            _ => "resting"
        };

        var memLevel = memPercent switch
        {
            > 90 => "wilted",
            > 70 => "thirsty",
            _ => "healthy"
        };

        var totalFeedback = _dpoPositive + _dpoNegative;
        var dpoRatio = totalFeedback > 0 ? (double)_dpoPositive / totalFeedback : 0.5;

        var color = cpuPercent switch
        {
            > 80 => "#ff9944",
            > 50 => "#88cc66",
            _ => "#aaccee"
        };

        var leafState = cpuPercent switch
        {
            > 90 => "wilted",
            > 60 => "drooping",
            _ => "vibrant"
        };

        var csharpThreads = ThreadPool.ThreadCount;
        var gcTotalMemory = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        var gcCollections = new Dictionary<string, int>
        {
            ["gen0"] = GC.CollectionCount(0),
            ["gen1"] = GC.CollectionCount(1),
            ["gen2"] = GC.CollectionCount(2)
        };

        _lastVitals = new Dictionary<string, object>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["uptime_seconds"] = (int)uptime.TotalSeconds,
            ["cpu"] = new Dictionary<string, object>
            {
                ["percent"] = Math.Round(cpuPercent, 1),
                ["level"] = cpuLevel,
                ["threads"] = csharpThreads
            },
            ["memory"] = new Dictionary<string, object>
            {
                ["percent"] = Math.Round(memPercent, 1),
                ["used_mb"] = Math.Round(memMb, 1),
                ["gc_mb"] = Math.Round(gcTotalMemory, 1),
                ["level"] = memLevel,
                ["collections"] = gcCollections
            },
            ["dpo"] = new Dictionary<string, object>
            {
                ["positive"] = _dpoPositive,
                ["negative"] = _dpoNegative,
                ["ratio"] = Math.Round(dpoRatio, 3)
            },
            ["led"] = new Dictionary<string, object>
            {
                ["color_hex"] = color,
                ["brightness"] = Math.Round(Math.Min(1.0, Math.Max(0.1, cpuPercent / 100)), 2),
                ["pulse_rate"] = cpuPercent > 60 ? "fast" : cpuPercent > 20 ? "normal" : "slow"
            },
            ["leaf_display"] = new Dictionary<string, object>
            {
                ["state"] = leafState,
                ["message"] = LeafMessage(cpuPercent, memLevel, dpoRatio)
            }
        };

        return _lastVitals;
    }

    public Dictionary<string, object> GetStats() => _lastVitals ?? Measure();

    public Dictionary<string, object> GetHardwareJson()
    {
        var v = Measure();
        return new Dictionary<string, object>
        {
            ["t"] = v["timestamp"],
            ["cpu"] = ((Dictionary<string, object>)v["cpu"])["percent"],
            ["cpu_l"] = ((Dictionary<string, object>)v["cpu"])["level"],
            ["mem"] = ((Dictionary<string, object>)v["memory"])["percent"],
            ["mem_l"] = ((Dictionary<string, object>)v["memory"])["level"],
            ["led"] = ((Dictionary<string, object>)v["led"])["color_hex"],
            ["led_b"] = ((Dictionary<string, object>)v["led"])["brightness"],
            ["led_p"] = ((Dictionary<string, object>)v["led"])["pulse_rate"],
            ["leaf"] = ((Dictionary<string, object>)v["leaf_display"])["state"],
            ["leaf_m"] = ((Dictionary<string, object>)v["leaf_display"])["message"]
        };
    }

    private static string LeafMessage(double cpu, string memLevel, double dpo)
    {
        if (cpu > 90)
            return "System is working hard... time to rest";
        if (memLevel == "wilted")
            return "Need more memory space";
        if (dpo > 0.8)
            return "System is happy with the feedback";
        return "System is growing steadily";
    }
}
