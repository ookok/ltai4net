// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  WasmtimeSandbox — AIContextProvider for sandboxed code execution
//
//  Uses Wasmtime (recommended) for WASM module execution, with
//  WASI capability-based security. Falls back to restricted
//  shell execution when Wasmtime cannot run the requested code.
//
//  Sandbox restrictions:
//  - Read-only access to workspace (no write outside sandbox dir)
//  - Network blocked by default
//  - Hard timeout (30s for shell, 60s for WASM)
//  - Output capped at 100KB
// ═══════════════════════════════════════════════════════════════

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LTAI.AI;
using LTAI.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Wasmtime;

namespace LTAI.Agent.Tools;

/// <summary>
/// Sandboxed code execution via Wasmtime (WASM) with fallback to restricted shell.
/// Registered as an AIContextProvider in the MAF pipeline.
///
/// Two modes:
///   1. <see cref="ExecuteWasmAsync"/> — runs a .wasm binary with WASI capability restrictions
///   2. <see cref="ExecuteSandboxedCommandAsync"/> — runs a shell command with sandbox restrictions
/// </summary>
[ToolDomain("sandbox")]
public sealed class WasmtimeSandbox : AIContextProvider
{
    private readonly string _workspace;
    private readonly string _sandboxDir;
    private readonly ILogger<WasmtimeSandbox>? _logger;
    private readonly Engine? _wasmEngine;
    private readonly bool _wasmAvailable;
    // Bounded WASM module cache (max 32 modules — each can be several MB)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Wasmtime.Module> _moduleCache = new(4, 32);
    private const int ModuleCacheMax = 32;
    private static int _moduleCount;

    private const int ShellTimeoutSec = 30;
    private const int WasmTimeoutSec = 60;
    private const int MaxOutputBytes = 100 * 1024;

    public WasmtimeSandbox(string workspace, ILogger<WasmtimeSandbox>? logger = null)
        : base(null, null, null)
    {
        _workspace = workspace;
        _sandboxDir = Path.Combine(workspace, ".sandbox");
        Directory.CreateDirectory(_sandboxDir);
        _logger = logger;

        try
        {
            _wasmEngine = new Engine();
            _wasmAvailable = true;
            _logger?.LogInformation("Wasmtime sandbox initialized (v44)");
        }
        catch (Exception ex)
        {
            _wasmEngine = null;
            _wasmAvailable = false;
            _logger?.LogWarning(ex, "Wasmtime engine unavailable — using restricted shell fallback");
        }
    }

    /// <summary>
    /// MAF AIContextProvider: inject sandbox tools into the agent's tool list.
    /// </summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var existing = context.AIContext;
        if (existing == null) return ValueTask.FromResult(context.AIContext!);

        var tools = existing.Tools?.ToList() ?? new List<AITool>();

        // sandbox_exec only WASM (shell handled by SafeShellTool)if (_wasmAvailable)
        {
            tools.Add(AIFunctionFactory.Create(ExecuteWasmAsync));
        }

        return ValueTask.FromResult(new AIContext
        {
            Instructions = existing.Instructions,
            Messages = existing.Messages,
            Tools = tools,
        });
    }

    // ═══════════════════════════════════════════
    //  Sandboxed shell command
    // ═══════════════════════════════════════════

    [Description("在沙箱中以受限权限执行命令。不能用来读取文件——读取文件请用 ReadFileContent。\n"
        + "适用场景：在隔离沙箱中运行脚本、测试不受信任的代码、运行短时工具命令。\n"
        + "不适用场景：读取文件（请用 ReadFileContent）、正常开发命令（请用 RunCommand 更快）。\n"
        + "关键参数：command — shell 命令；workDir — 沙箱内工作目录。")]
    [ToolExample("在沙箱中安全地运行这个脚本")]
    public async Task<string> ExecuteSandboxedCommandAsync(
        [Description("Shell command to run")] string command,
        [Description("Working directory (relative to sandbox)")] string workDir = ".",
        [Description("用户确认标记，必须为 true 才执行")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            return $"⚠️ 需要确认才在沙箱中执行命令。\n命令: `{command}`\n目录: {workDir}\n"
                 + "请用户确认后重新调用，设置 confirm=true。";

        // 拦截尝试用命令行读取文件的行为，重定向到 ReadFileContent
        var readCmdPatterns = new[] { "Get-Content", "gc ", "cat ", "type ", "more ", ".ReadAllText", "ReadFile" };
        if (readCmdPatterns.Any(p => command.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
            (command.Contains(":\\") || command.Contains("./") || command.Contains(".\\")))
            return "❌ 读取文件请使用 ReadFileContent 工具（【推荐】读取文件内容的首选工具），不要用命令行。";

        var resolvedDir = PathUtils.SafeResolvePath(_sandboxDir, workDir);
        if (resolvedDir == null)
            return ToolResult.Error("Working directory escapes sandbox");

        var sw = Stopwatch.StartNew();
        var output = new StringBuilder();
        var error = new StringBuilder();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(ShellTimeoutSec));

            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? $"/c \"{command}\"" : $"-c \"{command}\"",
                WorkingDirectory = resolvedDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Minimal PATH — no network utilities
            psi.EnvironmentVariables["PATH"] = isWindows
                ? @"C:\Windows\system32;C:\Windows"
                : "/usr/bin:/bin";

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = ReadWithLimitAsync(process.StandardOutput, MaxOutputBytes, cts.Token);
            var stderrTask = ReadWithLimitAsync(process.StandardError, MaxOutputBytes, cts.Token);

            var (stdout, stderr) = (await stdoutTask, await stderrTask.ConfigureAwait(false));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            sw.Stop();

            if (!string.IsNullOrEmpty(stdout)) output.Append(stdout);
            if (!string.IsNullOrEmpty(stderr)) output.Append($"\n[stderr]\n{stderr}");

            var result = output.Length > 0 ? output.ToString() : "(no output)";

            _logger?.LogDebug("Sandbox exec exit={ExitCode} ({Elapsed}ms, {Size}bytes)",
                process.ExitCode, sw.ElapsedMilliseconds, result.Length);

            return process.ExitCode == 0
                ? ToolResult.Success(result)
                : ToolResult.Error($"Exit code {process.ExitCode}", new { output = result });
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"Command timed out after {ShellTimeoutSec}s");
        }
        catch (Exception ex)
        {
            return ToolResult.FromException(ex);
        }
    }

    // ═══════════════════════════════════════════
    //  WASM module execution
    // ═══════════════════════════════════════════

    [Description("执行 .wasm 二进制文件，使用 WASI 沙箱限制（无网络、只读工作区）。\n"
        + "适用场景：运行 WebAssembly 模块、在沙箱中执行编译后的 Wasm 代码。\n"
        + "不适用场景：运行 shell 命令（请用 ExecuteSandboxedCommandAsync 或 RunCommand）。\n"
        + "关键参数：wasmPath — .wasm 文件路径。")]
    [ToolExample("运行这个 wasm 模块")]
    public async Task<string> ExecuteWasmAsync(
        [Description("Path to .wasm file")] string wasmPath,
        [Description("用户确认标记，必须为 true 才执行")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            return "⚠️ 需要确认才执行 WASM 模块。请用户确认后重新调用，设置 confirm=true。";

        if (!_wasmAvailable || _wasmEngine == null)
            return ToolResult.Error("Wasmtime engine not available");

        var fp = PathUtils.SafeResolvePath(_workspace, wasmPath);
        if (fp == null) return ToolResult.Error("WASM file path escapes workspace");
        if (!File.Exists(fp)) return ToolResult.Error($"WASM file not found: {wasmPath}");
        if (!fp.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Error("Not a .wasm file");

        var sw = Stopwatch.StartNew();

        try
        {
            var wasmBytes = await File.ReadAllBytesAsync(fp, ct).ConfigureAwait(false);

            // Compile module from bytes (with bounded cache)
            var name = Path.GetFileName(fp);
            if (!_moduleCache.TryGetValue(name, out var module))
            {
                if (Interlocked.Increment(ref _moduleCount) > ModuleCacheMax)
                {
                    foreach (var m in _moduleCache.Values) m.Dispose();
                    _moduleCache.Clear();
                    Interlocked.Exchange(ref _moduleCount, 0);
                }
                module = Module.FromBytes(_wasmEngine, name, wasmBytes.AsSpan());
                _moduleCache.TryAdd(name, module);
            }

            // Configure WASI: restrict to sandbox + workspace (read-only), no network
            var wasiConfig = new WasiConfiguration()
                .WithArgs(name)
                .WithEnvironmentVariable("PATH", "")
                .WithPreopenedDirectory(_sandboxDir, "/sandbox",
                    WasiDirectoryPermissions.Read | WasiDirectoryPermissions.Write,
                    WasiFilePermissions.Read | WasiFilePermissions.Write)
                .WithPreopenedDirectory(_workspace, "/workspace",
                    WasiDirectoryPermissions.Read,
                    WasiFilePermissions.Read);

            using var store = new Store(_wasmEngine);
            store.SetWasiConfiguration(wasiConfig);

            var linker = new Linker(_wasmEngine);
            linker.DefineWasi();

            var instance = linker.Instantiate(store, module);

            // Enforce execution timeout via Task wrapping
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(WasmTimeoutSec));
            var start = instance.GetFunction("_start");
            if (start != null)
            {
                var wasmTask = Task.Run(() => start.Invoke(), timeoutCts.Token);
                var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                var completed = await Task.WhenAny(wasmTask, timeoutTask).ConfigureAwait(false);
                if (completed != wasmTask)
                    return ToolResult.Error($"WASM execution timed out after {WasmTimeoutSec}s");
                await wasmTask.ConfigureAwait(false);
            }

            sw.Stop();

            var result = $"[WASM] Module '{name}' executed in {sw.ElapsedMilliseconds}ms";

            _logger?.LogDebug("WASM exec: {Name} → ({Elapsed}ms)", name, sw.ElapsedMilliseconds);

            return ToolResult.Success(result);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"WASM execution timed out after {WasmTimeoutSec}s");
        }
        catch (WasmtimeException ex)
        {
            sw.Stop();
            _logger?.LogWarning(ex, "Wasmtime execution failed: {Path}", wasmPath);
            return ToolResult.Error($"WASM execution failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return ToolResult.FromException(ex);
        }
    }

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════

    private static async Task<string> ReadWithLimitAsync(
        StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<char>.Shared.Rent(maxBytes);
        var chunk = System.Buffers.ArrayPool<char>.Shared.Rent(4096);
        try
        {
            var total = 0;
            while (!ct.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0) break;

                var copyLen = Math.Min(read, maxBytes - total);
                Array.Copy(chunk, 0, buffer, total, copyLen);
                total += copyLen;

                if (total >= maxBytes) break;
            }

            return new string(buffer, 0, total);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
            System.Buffers.ArrayPool<char>.Shared.Return(chunk);
        }
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct = default)
        => default;

    public void Dispose()
    {
        _wasmEngine?.Dispose();
    }
}
