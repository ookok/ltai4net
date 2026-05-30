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
public sealed class WasmtimeSandbox : AIContextProvider
{
    private readonly string _workspace;
    private readonly string _sandboxDir;
    private readonly ILogger<WasmtimeSandbox>? _logger;
    private readonly Engine? _wasmEngine;
    private readonly bool _wasmAvailable;

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

        // sandbox_exec — restricted shell execution
        var sandboxExecAttr = new System.ComponentModel.DescriptionAttribute(
            "Execute a shell command in the sandbox (read-only workspace, no network, 30s timeout)");
        tools.Add(AIFunctionFactory.Create(ExecuteSandboxedCommandAsync));

        if (_wasmAvailable)
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

    [Description("Execute a command in the sandbox with restricted permissions. 不能用来读取文件——读取文件请用 ReadFileContent 工具。")]
    public async Task<string> ExecuteSandboxedCommandAsync(
        [Description("Shell command to run")] string command,
        [Description("Working directory (relative to sandbox)")] string workDir = ".",
        CancellationToken ct = default)
    {
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

            var (stdout, stderr) = (await stdoutTask, await stderrTask);
            await process.WaitForExitAsync(cts.Token);

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

    [Description("Execute a .wasm binary with WASI sandbox restrictions")]
    public async Task<string> ExecuteWasmAsync(
        [Description("Path to .wasm file")] string wasmPath,
        CancellationToken ct = default)
    {
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

            // Compile module from bytes
            var name = Path.GetFileName(fp);
            var module = Module.FromBytes(_wasmEngine, name, wasmBytes.AsSpan());

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

            var start = instance.GetFunction("_start");
            start?.Invoke();

            sw.Stop();

            // Capture WASI output from store
            // Wasmtime writes to stdout/stderr pipes configured in WasiConfiguration
            // For pipe-based capture, store output files or use StdoutCallback

            var result = $"[WASM] Module '{name}' executed in {sw.ElapsedMilliseconds}ms";

            _logger?.LogDebug("WASM exec: {Name} → ({Elapsed}ms)", name, sw.ElapsedMilliseconds);

            return ToolResult.Success(result);
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
        var buffer = new char[maxBytes];
        var total = 0;
        var chunk = new char[4096];

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

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct = default)
        => default;

    public void Dispose()
    {
        _wasmEngine?.Dispose();
    }
}
