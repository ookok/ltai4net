using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record ResourceRequirement
{
    public int MemoryMb { get; init; }
    public double CpuPercent { get; init; }
    public int DiskMb { get; init; }
    public int ProcessCount { get; init; }
}

public sealed record ResourceAllocation
{
    public string AllocationId { get; init; } = Guid.NewGuid().ToString("N");
    public int MemoryMb { get; init; }
    public double CpuPercent { get; init; }
    public int ProcessCount { get; init; }
    public DateTime AllocatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record ResourceUsage
{
    public long TotalMemoryMb { get; init; }
    public long UsedMemoryMb { get; init; }
    public long AvailableMemoryMb { get; init; }
    public double CpuUsagePercent { get; init; }
    public long TotalDiskMb { get; init; }
    public long UsedDiskMb { get; init; }
    public int ProcessCount { get; init; }
    public string Platform { get; init; } = "";
}

public sealed class ResourceLimits
{
    public int MaxMemoryMb { get; set; }
    public double MaxCpuPercent { get; set; }
    public int MaxDiskMb { get; set; }
    public int MaxProcesses { get; set; }
}

public sealed class ResourceGuard
{
    private readonly ILogger<ResourceGuard> _logger;
    private readonly ConcurrentDictionary<string, ResourceAllocation> _allocations = new();
    private readonly ConcurrentDictionary<string, DateTime> _sandboxMemory = new();
    private ResourceLimits _limits = new();

    public ResourceLimits Limits
    {
        get => _limits;
        set => _limits = value;
    }

    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public ResourceGuard(ILogger<ResourceGuard>? logger = null)
    {
        _logger = logger ?? NullLogger<ResourceGuard>.Instance;
    }

    public bool IsAvailable()
    {
        return true;
    }

    public ResourceAllocation? Allocate(ResourceRequirement requirement)
    {
        try
        {
            if (IsExhausted(requirement))
                return null;

            var allocation = new ResourceAllocation
            {
                MemoryMb = requirement.MemoryMb,
                CpuPercent = requirement.CpuPercent,
                ProcessCount = requirement.ProcessCount
            };

            if (!_allocations.TryAdd(allocation.AllocationId, allocation))
                return null;

            _logger.LogInformation("Resource allocated: {Id} Memory={MemoryMb}MB CPU={CpuPercent}%",
                allocation.AllocationId, allocation.MemoryMb, allocation.CpuPercent);

            return allocation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to allocate resources");
            return null;
        }
    }

    public void Release(ResourceAllocation allocation)
    {
        if (allocation == null) return;

        _allocations.TryRemove(allocation.AllocationId, out _);
        _logger.LogInformation("Resource released: {Id}", allocation.AllocationId);
    }

    public ResourceUsage GetCurrentUsage()
    {
        try
        {
            if (IsLinux) return GetLinuxUsage();
            if (IsMacOS) return GetMacOSUsage();
            if (IsWindows) return GetWindowsUsage();

            return new ResourceUsage { Platform = "unknown" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get resource usage");
            return new ResourceUsage { Platform = DetectPlatform() };
        }
    }

    public ResourceUsage GetAvailableResources()
    {
        var usage = GetCurrentUsage();

        var allocatedMemory = _allocations.Values.Sum(a => a.MemoryMb);
        var allocatedCpu = _allocations.Values.Sum(a => a.CpuPercent);
        var sandboxMemory = _sandboxMemory.Count > 0
            ? (long)(_sandboxMemory.Count * 512)
            : 0;

        return usage with
        {
            UsedMemoryMb = usage.UsedMemoryMb + allocatedMemory + sandboxMemory,
            AvailableMemoryMb = Math.Max(0, usage.TotalMemoryMb - usage.UsedMemoryMb - allocatedMemory),
            CpuUsagePercent = Math.Min(100, usage.CpuUsagePercent + allocatedCpu),
            ProcessCount = usage.ProcessCount + _allocations.Values.Sum(a => a.ProcessCount)
        };
    }

    public bool IsExhausted(ResourceRequirement requirement)
    {
        var available = GetAvailableResources();

        if (_limits.MaxMemoryMb > 0 && available.AvailableMemoryMb < requirement.MemoryMb)
        {
            _logger.LogWarning("Memory exhausted: available {AvailableMB}MB, required {RequiredMB}MB",
                available.AvailableMemoryMb, requirement.MemoryMb);
            return true;
        }

        if (_limits.MaxCpuPercent > 0 && (100 - available.CpuUsagePercent) < requirement.CpuPercent)
        {
            _logger.LogWarning("CPU exhausted: available {AvailablePercent}%, required {RequiredPercent}%",
                100 - available.CpuUsagePercent, requirement.CpuPercent);
            return true;
        }

        if (_limits.MaxDiskMb > 0 && available.AvailableMemoryMb < requirement.DiskMb)
        {
            _logger.LogWarning("Disk exhausted: available {AvailableMB}MB, required {RequiredMB}MB",
                available.AvailableMemoryMb, requirement.DiskMb);
            return true;
        }

        if (_limits.MaxProcesses > 0 && available.ProcessCount >= _limits.MaxProcesses)
        {
            _logger.LogWarning("Process limit reached: {Current}/{Max}",
                available.ProcessCount, _limits.MaxProcesses);
            return true;
        }

        return false;
    }

    internal void TrackSandboxMemory(string sandboxId)
    {
        _sandboxMemory[sandboxId] = DateTime.UtcNow;
    }

    internal void UntrackSandboxMemory(string sandboxId)
    {
        _sandboxMemory.TryRemove(sandboxId, out _);
    }

    public IEnumerable<ResourceAllocation> GetActiveAllocations() => _allocations.Values;

    private ResourceUsage GetLinuxUsage()
    {
        var totalMemMb = ReadMemInfo("MemTotal") / 1024L;
        var availMemMb = ReadMemInfo("MemAvailable") / 1024L;
        var usedMemMb = totalMemMb - availMemMb;
        var cpuPercent = ReadCpuUsage();
        var diskInfo = GetDiskUsage("/");
        var procCount = GetProcessCount();

        return new ResourceUsage
        {
            TotalMemoryMb = totalMemMb,
            UsedMemoryMb = usedMemMb,
            AvailableMemoryMb = availMemMb,
            CpuUsagePercent = cpuPercent,
            TotalDiskMb = diskInfo.total,
            UsedDiskMb = diskInfo.used,
            ProcessCount = procCount,
            Platform = "linux"
        };
    }

    private ResourceUsage GetMacOSUsage()
    {
        try
        {
            var totalMemMb = RunSysCtl("hw.memsize", 0) / (1024L * 1024L);
            var pageSize = RunSysCtl("vm.pagesize", 4096) / 1024L;
            var freePages = RunSysCtl("vm.page_free_count", 0);

            var freeMemMb = freePages * pageSize / 1024L;
            var usedMemMb = totalMemMb - freeMemMb;
            var cpuPercent = ReadCpuUsage();
            var diskInfo = GetDiskUsage("/");
            var procCount = GetProcessCount();

            return new ResourceUsage
            {
                TotalMemoryMb = totalMemMb,
                UsedMemoryMb = usedMemMb,
                AvailableMemoryMb = freeMemMb,
                CpuUsagePercent = cpuPercent,
                TotalDiskMb = diskInfo.total,
                UsedDiskMb = diskInfo.used,
                ProcessCount = procCount,
                Platform = "macos"
            };
        }
        catch
        {
            return new ResourceUsage { Platform = "macos" };
        }
    }

    private ResourceUsage GetWindowsUsage()
    {
        try
        {
            var gcMemInfo = GC.GetGCMemoryInfo();
            var totalMemMb = gcMemInfo.TotalAvailableMemoryBytes / (1024L * 1024L);

            using var proc = Process.GetCurrentProcess();
            var workingSetMb = proc.WorkingSet64 / (1024L * 1024L);

            var cpuPercent = ReadCpuUsage();
            var diskInfo = GetDiskUsage(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\");
            var procCount = GetProcessCount();

            return new ResourceUsage
            {
                TotalMemoryMb = totalMemMb,
                UsedMemoryMb = workingSetMb,
                AvailableMemoryMb = totalMemMb - workingSetMb,
                CpuUsagePercent = cpuPercent,
                TotalDiskMb = diskInfo.total,
                UsedDiskMb = diskInfo.used,
                ProcessCount = procCount,
                Platform = "windows"
            };
        }
        catch
        {
            return new ResourceUsage { Platform = "windows" };
        }
    }

    private static long ReadMemInfo(string key)
    {
        try
        {
            var meminfo = File.ReadAllText("/proc/meminfo");
            foreach (var line in meminfo.Split('\n'))
            {
                if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Split(':', StringSplitOptions.TrimEntries)[1]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                return long.Parse(value);
            }
        }
        catch { }
        return 0;
    }

    private static double ReadCpuUsage()
    {
        try
        {
            if (File.Exists("/proc/stat"))
            {
                var stat = File.ReadAllText("/proc/stat");
                var cpuLine = stat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu "));
                if (cpuLine != null)
                {
                    var parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
                    var total = parts.Sum();
                    var idle = parts[3];
                    return Math.Round((1.0 - (double)idle / total) * 100, 1);
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "wmic" : "top",
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "cpu get loadpercentage /value"
                    : "-bn1 | grep \"Cpu(s)\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(3000);
                var output = proc.StandardOutput.ReadToEnd();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var match = Regex.Match(output, @"LoadPercentage=(\d+)");
                    if (match.Success)
                        return double.Parse(match.Groups[1].Value);
                }
            }
        }
        catch { }
        return 0;
    }

    private static (long total, long used) GetDiskUsage(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path) ?? path;
            if (string.IsNullOrEmpty(root)) return (0, 0);

            var driveInfo = new DriveInfo(root);
            if (!driveInfo.IsReady) return (0, 0);

            var total = driveInfo.TotalSize / (1024L * 1024L);
            var free = driveInfo.AvailableFreeSpace / (1024L * 1024L);
            var used = total - free;
            return (total, used);
        }
        catch { }
        return (0, 0);
    }

    private static int GetProcessCount()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new ProcessStartInfo("ps", "-e --no-headers")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(3000);
                    var output = proc.StandardOutput.ReadToEnd();
                    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                }
            }
            return Process.GetProcesses().Length;
        }
        catch { }
        return 0;
    }

    private static long RunSysCtl(string key, long defaultValue)
    {
        try
        {
            var psi = new ProcessStartInfo("sysctl", $"-n {key}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(3000);
                var output = proc.StandardOutput.ReadToEnd().Trim();
                if (long.TryParse(output, out var val))
                    return val;
            }
        }
        catch { }
        return defaultValue;
    }

    private static string DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        return "unknown";
    }
}
