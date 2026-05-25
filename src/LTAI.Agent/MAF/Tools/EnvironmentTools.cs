using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LTAI.Agent.Tools;

[Description("System environment, diagnostics, and process information tools")]
public sealed class EnvironmentTools
{
    [Description("Get detailed system information: OS, memory, CPU, drives, environment variables.")]
    public static string GetSystemInfo()
    {
        var process = Process.GetCurrentProcess();
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new
        {
            name = d.Name,
            label = d.VolumeLabel,
            format = d.DriveFormat,
            totalGB = Math.Round(d.TotalSize / 1024.0 / 1024 / 1024, 1),
            availableGB = Math.Round(d.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1),
            usedPercent = d.TotalSize > 0 ? Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize, 1) : 0
        });

        return JsonSerializer.Serialize(new
        {
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            is64Bit = Environment.Is64BitOperatingSystem,
            machineName = Environment.MachineName,
            processorCount = Environment.ProcessorCount,
            currentDirectory = Environment.CurrentDirectory,
            systemDirectory = Environment.SystemDirectory,
            uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            memory = new
            {
                workingSetMB = Math.Round(process.WorkingSet64 / 1024.0 / 1024, 1),
                privateMemoryMB = Math.Round(process.PrivateMemorySize64 / 1024.0 / 1024, 1),
                gcTotalMemoryMB = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024, 1)
            },
            process = new
            {
                id = process.Id,
                name = process.ProcessName,
                threads = process.Threads.Count,
                startTime = process.StartTime.ToString("O"),
                totalProcessorTime = process.TotalProcessorTime.ToString()
            },
            drives = drives.ToList()
        });
    }

    [Description("Get environment variable value. Returns null if not set.")]
    public static string GetEnvironmentVariable(
        [Description("Environment variable name")] string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return JsonSerializer.Serialize(new { name, found = false });
        var maskedValue = name.Contains("KEY", StringComparison.OrdinalIgnoreCase) || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
            ? value[..Math.Min(4, value.Length)] + "***"
            : value;
        return JsonSerializer.Serialize(new { name, found = true, value = maskedValue });
    }

    [Description("List running processes. Returns process name, ID, and memory usage sorted by memory.")]
    public static string ListProcesses(
        [Description("Filter by process name (partial match)")] string? filter = null,
        [Description("Max number of results")] int top = 20)
    {
        try
        {
            var processes = Process.GetProcesses()
                .Where(p => string.IsNullOrEmpty(filter) || p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.WorkingSet64)
                .Take(top)
                .Select(p =>
                {
                    try { return new { id = p.Id, name = p.ProcessName, memoryMB = Math.Round(p.WorkingSet64 / 1024.0 / 1024, 1), threads = p.Threads.Count }; }
                    catch { return new { id = p.Id, name = p.ProcessName, memoryMB = 0.0, threads = 0 }; }
                });

            return JsonSerializer.Serialize(new { filter, top, processes = processes.ToList() });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Get network information: hostname, IP addresses, and test connectivity to a host.")]
    public static async Task<string> GetNetworkInfo(
        [Description("Optional hostname to ping")] string? pingHost = null,
        CancellationToken cancellationToken = default)
    {
        var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        var result = new Dictionary<string, object?>
        {
            ["hostName"] = hostEntry.HostName,
            ["ipAddresses"] = hostEntry.AddressList.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).Select(a => a.ToString()).ToList()
        };

        if (!string.IsNullOrWhiteSpace(pingHost))
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(pingHost, 3000).ConfigureAwait(false);
                sw.Stop();
                result["ping"] = new { host = pingHost, status = reply.Status.ToString(), roundtripMs = sw.ElapsedMilliseconds, address = reply.Address?.ToString() };
            }
            catch (Exception ex)
            {
                result["ping"] = new { host = pingHost, error = ex.Message };
            }
        }

        return JsonSerializer.Serialize(result);
    }
}
