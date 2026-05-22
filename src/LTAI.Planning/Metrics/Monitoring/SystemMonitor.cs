using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Metrics.Monitoring
{
    public record ResourceSnapshot(
        double CpuPercent,
        double MemoryPercent,
        double DiskPercent,
        long MemoryAvailableMb,
        DateTime Timestamp)
    {
        public bool UnderPressure => CpuPercent > 80 || MemoryPercent > 85;
        public bool Critical => CpuPercent > 90 || MemoryPercent > 95;
    }

    public sealed class SystemMonitor
    {
        private static readonly Lazy<SystemMonitor> _lazyInstance =
            new Lazy<SystemMonitor>(() => new SystemMonitor());

        public static SystemMonitor Instance => _lazyInstance.Value;

        private readonly ILogger<SystemMonitor> _logger;

        private ResourceSnapshot? _cached;
        private TimeSpan _previousCpuTime;
        private DateTime _previousCpuSampleTime;
        private bool _hasCpuBaseline;

        private int _consecutiveSkips;

        public int CpuHighThreshold { get; set; } = 80;
        public int MemHighThreshold { get; set; } = 85;
        public int MemLowMb { get; set; } = 512;
        public int CpuIdleThreshold { get; set; } = 30;

        public SystemMonitor(ILogger<SystemMonitor>? logger = null)
        {
            _logger = logger ?? NullLogger<SystemMonitor>.Instance;
        }

        public ResourceSnapshot Snapshot()
        {
            if (_cached != null
                && (DateTime.UtcNow - _cached.Timestamp).TotalSeconds < 5)
            {
                return _cached;
            }

            var process = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;

            double cpuPercent = 0;
            var currentCpuTime = process.TotalProcessorTime;
            if (_hasCpuBaseline)
            {
                var elapsedMs = (now - _previousCpuSampleTime).TotalMilliseconds;
                var cpuDeltaMs = (currentCpuTime - _previousCpuTime).TotalMilliseconds;
                if (elapsedMs > 0)
                {
                    cpuPercent = cpuDeltaMs / (Environment.ProcessorCount * elapsedMs) * 100;
                }
            }
            _previousCpuTime = currentCpuTime;
            _previousCpuSampleTime = now;
            _hasCpuBaseline = true;

            long totalPhysicalMemory = 16L * 1024 * 1024 * 1024;
            try
            {
                totalPhysicalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }
            catch { /* non-fatal */ }

            double memoryPercent = (double)GC.GetTotalMemory(false) / totalPhysicalMemory * 100;
            double diskPercent = 0;
            long memoryAvailableMb =
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;

            var snapshot = new ResourceSnapshot(
                cpuPercent,
                memoryPercent,
                diskPercent,
                memoryAvailableMb,
                now);

            _cached = snapshot;
            return snapshot;
        }

        public bool CanRunTask(string taskName, bool heavy = false)
        {
            var snapshot = Snapshot();

            if (snapshot.Critical)
            {
                Interlocked.Increment(ref _consecutiveSkips);
                return false;
            }

            if (snapshot.CpuPercent > CpuHighThreshold && !heavy)
            {
                return false;
            }

            if (heavy && snapshot.CpuPercent > (100 - CpuIdleThreshold))
            {
                return false;
            }

            if (snapshot.MemoryPercent > MemHighThreshold)
            {
                return false;
            }

            if (snapshot.MemoryAvailableMb < MemLowMb)
            {
                return false;
            }

            Interlocked.Exchange(ref _consecutiveSkips, 0);
            return true;
        }

        public Dictionary<string, object> GetStats()
        {
            var snapshot = Snapshot();

            return new Dictionary<string, object>
            {
                ["snapshot"] = new Dictionary<string, object>
                {
                    ["cpu_percent"] = snapshot.CpuPercent,
                    ["memory_percent"] = snapshot.MemoryPercent,
                    ["disk_percent"] = snapshot.DiskPercent,
                    ["memory_available_mb"] = snapshot.MemoryAvailableMb,
                    ["timestamp"] = snapshot.Timestamp,
                    ["under_pressure"] = snapshot.UnderPressure,
                    ["critical"] = snapshot.Critical
                },
                ["consecutive_skips"] = _consecutiveSkips,
                ["thresholds"] = new Dictionary<string, object>
                {
                    ["cpu_high"] = CpuHighThreshold,
                    ["mem_high"] = MemHighThreshold,
                    ["mem_low_mb"] = MemLowMb,
                    ["cpu_idle"] = CpuIdleThreshold
                }
            };
        }
    }
}
