using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

public sealed class SystemTools
{
    private static readonly Lazy<HttpClient> _sharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    [Description("REQUIRED: Get the ACTUAL current date and time from the system clock. ALWAYS call this tool when the user asks what day it is, what time it is, today's date, or the current weekday. Do NOT guess or estimate the date.")]
    public static string GetCurrentDateTime()
    {
        var now = DateTime.Now;
        var weekday = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            DayOfWeek.Sunday => "星期日",
            _ => now.DayOfWeek.ToString()
        };
        return $"当前时间: {now:yyyy-MM-dd HH:mm:ss}\n当前日期: {now:yyyy年MM月dd日}\n星期: {weekday}";
    }

    [Description("Get system information: OS, CPU, memory, disk, runtime")]
    public static string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## System Information\n");
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| OS | {RuntimeInformation.OSDescription} |");
        sb.AppendLine($"| OS Arch | {RuntimeInformation.OSArchitecture} |");
        sb.AppendLine($"| Process Arch | {RuntimeInformation.ProcessArchitecture} |");
        sb.AppendLine($"| .NET | {RuntimeInformation.FrameworkDescription} |");
        sb.AppendLine($"| Machine | {Environment.MachineName} |");
        sb.AppendLine($"| User | {Environment.UserName} |");
        sb.AppendLine($"| CPUs | {Environment.ProcessorCount} cores |");
        sb.AppendLine($"| Process | {Environment.ProcessId} |");
        sb.AppendLine($"| Uptime | {TimeSpan.FromMilliseconds(Environment.TickCount64):dd\\.hh\\:mm\\:ss} |");
        sb.AppendLine($"| Working Set | {FormatSize(Environment.WorkingSet)} |");
        sb.AppendLine($"| System Dir | {Environment.SystemDirectory} |");
        sb.AppendLine($"| Current Dir | {Environment.CurrentDirectory} |");
        sb.AppendLine($"| User Temp | {Path.GetTempPath()} |");
        sb.AppendLine($"| CLR Version | {Environment.Version} |");
        sb.AppendLine($"| 64-bit OS | {Environment.Is64BitOperatingSystem} |");
        sb.AppendLine($"| 64-bit Process | {Environment.Is64BitProcess} |");

        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                var pct = drive.TotalSize > 0
                    ? $"({(double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100:F0}% used)"
                    : "";
                sb.AppendLine($"| Disk {drive.Name} | {FormatSize(drive.AvailableFreeSpace)} free / {FormatSize(drive.TotalSize)} total {pct} |");
            }
        }
        catch { }

        return sb.ToString();
    }

    [Description("List running processes")]
    public static string ListProcesses(
        [Description("Filter by process name (optional)")] string? filter = null,
        [Description("Max results (1-100)")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var sb = new StringBuilder();
        sb.AppendLine("## Running Processes\n");
        sb.AppendLine("| PID | Name | CPU (s) | Memory | Threads |");
        sb.AppendLine("|-----|------|---------|--------|---------|");

        try
        {
            var processes = string.IsNullOrEmpty(filter)
                ? Process.GetProcesses()
                : Process.GetProcessesByName(filter);

            var now = DateTime.Now;
            foreach (var p in processes.Take(limit))
            {
                try
                {
                    var cpu = (now - p.StartTime).TotalSeconds > 0
                        ? $"{(p.TotalProcessorTime.TotalSeconds / (now - p.StartTime).TotalSeconds * 100):F1}%"
                        : "N/A";
                    sb.AppendLine($"| {p.Id} | {p.ProcessName} | {cpu} | {FormatSize(p.WorkingSet64)} | {p.Threads.Count} |");
                }
                catch { }
                finally { p.Dispose(); }
            }

            if (processes.Length > limit)
                sb.AppendLine($"\n... and {processes.Length - limit} more");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"| Error: {ex.Message} |");
        }

        return sb.ToString();
    }

    [Description("Get environment variables")]
    public static string GetEnv(
        [Description("Variable name (empty = list all)")] string? name = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(name))
        {
            var val = Environment.GetEnvironmentVariable(name);
            if (val == null) return $"Variable '{name}' not set.";
            if (name.Contains("KEY") || name.Contains("SECRET") || name.Contains("PASSWORD") || name.Contains("TOKEN"))
                val = val.Length > 8 ? val[..8] + "..." : "***";
            return $"**{name}** = `{val}`";
        }

        sb.AppendLine("## Environment Variables\n");
        sb.AppendLine("| Variable | Value |");
        sb.AppendLine("|----------|-------|");

        foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key?.ToString() ?? "";
            var val = de.Value?.ToString() ?? "";
            if (key.Contains("KEY") || key.Contains("SECRET") || key.Contains("PASSWORD") || key.Contains("TOKEN"))
                val = val.Length > 8 ? val[..8] + "..." : "***";
            if (val.Length > 60) val = val[..60] + "...";
            sb.AppendLine($"| {key} | {val} |");
        }

        return sb.ToString();
    }

    [Description("设置环境变量。必须传入 confirmed=true 才能执行——AI 必须先向用户展示变更并取得确认。")]
    public static string SetEnv(
        [Description("变量名")] string name,
        [Description("变量值")] string value,
        [Description("必须为 true 才会实际执行设置")] bool confirmed = false)
    {
        if (!confirmed)
            return "⚠️ 设置环境变量需要用户确认。请向用户展示要设置的变量名和值，确认后调用 SetEnv 并传入 confirmed=true。";

        Environment.SetEnvironmentVariable(name, value);
        var preview = name.Contains("KEY") || name.Contains("SECRET") || name.Contains("PASSWORD") || name.Contains("TOKEN")
            ? value.Length > 8 ? value[..8] + "..." : "***"
            : value;
        return $"✅ `{name}` = `{preview}`  (仅当前进程有效，重启后丢失)";
    }

    [Description("Ping a host to check connectivity")]
    public static async Task<string> Ping(
        [Description("Hostname or IP address")] string host,
        [Description("Timeout in ms")] int timeoutMs = 3000)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, Math.Clamp(timeoutMs, 100, 30000)).ConfigureAwait(false);

            return reply.Status == IPStatus.Success
                ? $"✅ **{host}** reachable — {reply.RoundtripTime}ms (TTL={reply.Options?.Ttl ?? 0})"
                : $"❌ **{host}** — {reply.Status}";
        }
        catch (Exception ex)
        {
            return $"❌ **{host}** — {ex.Message}";
        }
    }

    [Description("DNS resolution: lookup IP addresses for a hostname")]
    public static async Task<string> DnsLookup(
        [Description("Hostname to resolve")] string host)
    {
        try
        {
            var entries = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"## DNS Lookup: {host}\n");

            foreach (var addr in entries)
                sb.AppendLine($"- {addr} ({(addr.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")})");

            try
            {
                var reverse = await Dns.GetHostEntryAsync(entries[0]).ConfigureAwait(false);
                sb.AppendLine($"\nPTR: {reverse.HostName}");
            }
            catch { }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"DNS lookup failed: {ex.Message}";
        }
    }

    [Description("Check if a TCP port is open on a host")]
    public static async Task<string> CheckPort(
        [Description("Hostname or IP")] string host,
        [Description("TCP port number")] int port,
        [Description("Timeout in ms")] int timeoutMs = 3000)
    {
        port = Math.Clamp(port, 1, 65535);
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false) == task && task.IsCompletedSuccessfully)
            {
                client.Close();
                return $"✅ Port **{port}** on **{host}** is OPEN";
            }
            return $"❌ Port **{port}** on **{host}** is CLOSED or filtered";
        }
        catch
        {
            return $"❌ Port **{port}** on **{host}** is CLOSED";
        }
    }

    [Description("HTTP health check: GET a URL and return status code + timing")]
    public static async Task<string> HttpCheck(
        [Description("URL to check")] string url,
        [Description("Timeout in seconds")] int timeoutSec = 10)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 60)));
            var sw = Stopwatch.StartNew();
            var resp = await _sharedHttp.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[2048];
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            var bodyPreview = Encoding.UTF8.GetString(buffer, 0, bytesRead)
                .Replace("\n", " ").Replace("\r", "");
            if (bodyPreview.Length > 200) bodyPreview = bodyPreview[..200] + "...";
            if (bytesRead == buffer.Length) bodyPreview += " [truncated]";

            return $"""
                ## HTTP Check: {url}

                | Metric | Value |
                |--------|-------|
                | Status | {(int)resp.StatusCode} {resp.ReasonPhrase} |
                | Time | {sw.ElapsedMilliseconds}ms |
                | Content-Type | {resp.Content.Headers.ContentType} |
                | Content-Length | {FormatSize(resp.Content.Headers.ContentLength ?? 0)} |
                | Body Preview | {bodyPreview} |
                """;
        }
        catch (Exception ex)
        {
            return $"❌ HTTP check failed: {ex.Message}";
        }
    }

    [Description("Show network interfaces and IP addresses")]
    public static string NetworkInterfaces()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Network Interfaces\n");
        sb.AppendLine("| Name | Type | Status | IP | MAC | Speed |");
        sb.AppendLine("|------|------|--------|-----|-----|-------|");

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var ipProps = ni.GetIPProperties();
            var ipv4 = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            var ipStr = ipv4?.ToString() ?? "-";
            var mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));

            sb.AppendLine($"| {ni.Name} | {ni.NetworkInterfaceType} | {ni.OperationalStatus} | {ipStr} | {mac} | {ni.Speed / 1000000} Mbps |");
        }

        return sb.ToString();
    }

    [Description("Get the CURRENT WORKING DIRECTORY path")]
    public static string GetCurrentDirectory()
    {
        return Directory.GetCurrentDirectory();
    }

    [Description("List files and subdirectories in a given path")]
    public static string ListDirectory(
        [Description("Directory path (default: current working directory)")] string path = ".")
    {
        try
        {
            var fp = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            if (!Directory.Exists(fp)) return $"Directory not found: {fp}";

            var sb = new StringBuilder();
            sb.AppendLine($"## 📂 {fp}\n");
            sb.AppendLine("| Name | Type | Size |");
            sb.AppendLine("|------|------|------|");

            foreach (var d in Directory.GetDirectories(fp).OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(d);
                var subDirs = Directory.GetDirectories(d).Length;
                var files = Directory.GetFiles(d).Length;
                sb.AppendLine($"| 📁 {name} | Directory | {subDirs} dirs, {files} files |");
            }

            foreach (var f in Directory.GetFiles(fp).OrderBy(Path.GetFileName))
            {
                var fi = new FileInfo(f);
                var size = fi.Length switch
                {
                    < 1024 => $"{fi.Length} B",
                    < 1048576 => $"{fi.Length / 1024.0:F1} KB",
                    _ => $"{fi.Length / 1048576.0:F1} MB"
                };
                sb.AppendLine($"| 📄 {fi.Name} | File | {size} |");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }
    }

    [Description("WHOIS lookup for domain registration info")]
    public static async Task<string> Whois(
        [Description("Domain name (e.g. example.com)")] string domain)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://rdap.org/domain/{domain}");
            req.Headers.UserAgent.ParseAdd("LTAI/1.0");
            using var httpResp = await _sharedHttp.Value.SendAsync(req, cts.Token).ConfigureAwait(false);
            httpResp.EnsureSuccessStatusCode();
            var resp = await httpResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(resp);

            var sb = new StringBuilder();
            sb.AppendLine($"## WHOIS: {domain}\n");

            if (doc.RootElement.TryGetProperty("events", out var events))
            {
                foreach (var e in events.EnumerateArray())
                {
                    var evtAction = e.GetProperty("eventAction").GetString();
                    var evtDate = e.GetProperty("eventDate").GetString();
                    sb.AppendLine($"- {evtAction}: {evtDate}");
                }
            }

            if (doc.RootElement.TryGetProperty("entities", out var entities))
            {
                foreach (var ent in entities.EnumerateArray().Take(3))
                {
                    if (ent.TryGetProperty("vcardArray", out var vcard))
                    {
                        foreach (var item in vcard.EnumerateArray().Skip(1).Take(5))
                        {
                            var arr = item.EnumerateArray().ToList();
                            if (arr.Count >= 3)
                                sb.AppendLine($"- {arr[1]}: {arr[3]}");
                        }
                    }
                }
            }

            return sb.Length > 30 ? sb.ToString() : $"No WHOIS data for {domain}";
        }
        catch (Exception ex)
        {
            return $"WHOIS lookup failed: {ex.Message}";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        < 1073741824 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB"
    };

    [Description("Run a command inside a Docker container. Requires Docker installed and running. 注意：Docker 容器可能绕过本地沙箱限制，请确认后再使用。")]
    public async Task<string> RunInContainer(
        [Description("Docker image name (e.g. 'ubuntu:latest', 'python:3.11')")] string image,
        [Description("Command to run inside the container")] string command,
        [Description("确认执行。Docker 容器可能绕过本地沙箱限制。")] bool confirm = false)
    {
        if (!confirm)
            return "⛔ 容器执行已取消：Docker 容器可能绕过沙箱限制，需设置 confirm=true 确认后执行。";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"run --rm {image} {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            process.WaitForExit(TimeSpan.FromMinutes(5));

            var sb = new StringBuilder();
            sb.AppendLine($"## Docker Run: {image}\n");
            sb.AppendLine($"Exit Code: {process.ExitCode}\n");
            if (!string.IsNullOrEmpty(output))
                sb.AppendLine("### Output\n" + output);
            if (!string.IsNullOrEmpty(error))
                sb.AppendLine("### Errors\n" + error);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Docker run failed: {ex.Message}";
        }
    }

    [Description("Run a command with full network access (bypass localhost-only restriction). 用于需要访问外部网络的命令。注意：此命令不受沙箱限制，请确认后再使用。")]
    public async Task<string> RunWithNetwork(
        [Description("Command to run")] string command,
        [Description("Timeout in seconds")] int timeoutSec = 60,
        [Description("确认执行。此命令不受沙箱限制，有安全风险。")] bool confirm = false)
    {
        if (!confirm)
            return "⛔ 执行已取消：RunWithNetwork 不受沙箱限制，需设置 confirm=true 确认后执行。";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash",
                Arguments = Environment.OSVersion.Platform == PlatformID.Win32NT ? $"/c {command}" : $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = psi };
            process.Start();

            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 600));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill();
                return $"Command timed out after {timeoutSec}s";
            }
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.AppendLine($"## Command: {command}\n");
            sb.AppendLine($"Exit Code: {process.ExitCode}\n");
            if (!string.IsNullOrEmpty(output))
                sb.AppendLine("### Output\n" + output);
            if (!string.IsNullOrEmpty(error))
                sb.AppendLine("### Errors\n" + error);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }

    [Description("Check if Docker is installed and the Docker daemon is running")]
    public async Task<string> CheckDockerAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format '{{json .}}'",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            process.WaitForExit(TimeSpan.FromSeconds(10));

            if (process.ExitCode != 0)
                return $"❌ Docker is not available: {error}";

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var version = root.TryGetProperty("ServerVersion", out var v) ? v.GetString() : "?";
            var containers = root.TryGetProperty("Containers", out var c) ? c.GetInt32() : 0;
            var running = root.TryGetProperty("ContainersRunning", out var r) ? r.GetInt32() : 0;
            var images = root.TryGetProperty("Images", out var i) ? i.GetInt32() : 0;
            var os = root.TryGetProperty("OperatingSystem", out var o) ? o.GetString() : "?";

            return $"""
                ✅ Docker is available

                | Metric | Value |
                |--------|-------|
                | Version | {version} |
                | OS | {os} |
                | Containers | {containers} (running: {running}) |
                | Images | {images} |
                """;
        }
        catch (Exception ex)
        {
            return $"❌ Docker check failed: {ex.Message}";
        }
    }
}
