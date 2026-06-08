using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.LanguageServer;

/// <summary>
/// Maps file extensions to LSP server commands and manages lifecycle
/// for all running LSP processes.
///
/// v2 增强: 支持 C# 语义诊断（通过 Roslyn CSharpDiagProvider），
/// 不需要外部 LSP 进程即可对 .cs 文件做完整类型检查。
/// </summary>
public sealed class LspLanguageManager : IDisposable
{
    private readonly ConcurrentDictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _fileToExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _rootUri;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(5);
    private readonly Timer? _cleanupTimer;
    private readonly CSharpDiagProvider? _csharpDiag;
    private readonly ILogger<LspLanguageManager>? _logger;

    private static readonly Dictionary<string, (string cmd, string args)> ExtToLsp = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mbt"] = ("moonbit", "lsp"),
        [".mojo"] = ("mojo", "lsp"),
        ["🔥"] = ("mojo", "lsp"),
        [".cj"] = ("cjc", "lsp"),
    };

    public LspLanguageManager(
        string? rootPath = null,
        CSharpDiagProvider? csharpDiag = null,
        ILogger<LspLanguageManager>? logger = null)
    {
        _rootUri = new Uri(rootPath ?? Directory.GetCurrentDirectory()).AbsoluteUri;
        _csharpDiag = csharpDiag;
        _logger = logger;
        _cleanupTimer = new Timer(_ => CleanupIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>List extensions that have LSP support.</summary>
    public static IEnumerable<string> SupportedExtensions => ExtToLsp.Keys;

    /// <summary>Whether the given file extension has an LSP server or in-process diagnostics.</summary>
    public static bool HasLsp(string ext) =>
        ExtToLsp.ContainsKey(ext) ||
        string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Open a file for diagnostics — starts LSP server on demand for non-C#, uses Roslyn for C#.</summary>
    public async Task OpenFileAsync(string filePath, string content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        if (ext == null) return;

        // C# 文件使用内置 Roslyn 诊断
        if (ext == ".cs")
        {
            _fileToExt[filePath] = ext;
            if (_csharpDiag != null)
            {
                _logger?.LogDebug("LspLanguageManager: opening C# file via Roslyn: {File}",
                    Path.GetFileName(filePath));
                await _csharpDiag.UpdateDocumentAsync(filePath, content, ct).ConfigureAwait(false);
            }
            return;
        }

        // 其他语言使用外部 LSP 进程
        if (!ExtToLsp.TryGetValue(ext, out var lspInfo)) return;

        var client = _clients.GetOrAdd(ext, CreateLspClient);

        if (client.IsRunning)
        {
            await client.DidOpenAsync(filePath, content, ct).ConfigureAwait(false);
            _fileToExt[filePath] = ext;
        }
    }

    private LspClient CreateLspClient(string ext)
    {
        if (!ExtToLsp.TryGetValue(ext, out var lspInfo))
            return new LspClient("", "");
        var c = new LspClient(lspInfo.cmd, lspInfo.args);
        System.Threading.Tasks.Task.Run(() => c.StartAsync(_rootUri, System.Threading.CancellationToken.None));
        return c;
    }

    /// <summary>Notify LSP that a file was closed — releases resources.</summary>
    public async Task CloseFileAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();

        // C# 文件 → 从 Roslyn 工作区移除
        if (ext == ".cs" && _csharpDiag != null)
        {
            _csharpDiag.RemoveDocument(filePath);
            _fileToExt.TryRemove(filePath, out _);
            _logger?.LogDebug("LspLanguageManager: closed C# file via Roslyn: {File}",
                Path.GetFileName(filePath));
            return;
        }

        // 其他语言
        if (ext != null && _fileToExt.TryRemove(filePath, out var fileExt) &&
            _clients.TryGetValue(fileExt, out var client))
        {
            // LSP 协议没有明确的 didClose 语义，大部分 LSP 不需要
            _logger?.LogDebug("LspLanguageManager: closed file: {File}", Path.GetFileName(filePath));
        }
    }

    /// <summary>Notify LSP of file changes — triggers re-diagnostics.</summary>
    public async Task UpdateFileAsync(string filePath, string content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();

        // C# 文件 → Roslyn 语义诊断
        if (ext == ".cs" && _csharpDiag != null)
        {
            _logger?.LogDebug("LspLanguageManager: updating C# file via Roslyn: {File}",
                Path.GetFileName(filePath));
            await _csharpDiag.UpdateDocumentAsync(filePath, content, ct).ConfigureAwait(false);
            _fileToExt[filePath] = ext;
            return;
        }

        // 其他语言 → 外部 LSP 进程
        if (ext != null && _fileToExt.TryGetValue(filePath, out var fileExt) &&
            _clients.TryGetValue(fileExt, out var client) && client.IsRunning)
        {
            await client.DidChangeAsync(filePath, content, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Get current diagnostics for all open files of a given extension.</summary>
    public List<(string filePath, LspDiagnostic diag)> GetDiagnostics(string? ext = null)
    {
        var results = new List<(string, LspDiagnostic)>();

        // 先收集 C# Roslyn 诊断
        if (_csharpDiag != null && (ext == null || ext == ".cs"))
        {
            foreach (var (filePath, fileExt) in _fileToExt)
            {
                if (fileExt != ".cs") continue;
                if (ext != null && !fileExt.Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var d in _csharpDiag.LastDiagnostics)
                {
                    if (string.IsNullOrEmpty(d.Source) || d.Source == "Roslyn")
                        results.Add((filePath, d));
                }
            }
        }

        // 再收集外部 LSP 进程诊断
        foreach (var (filePath, fileExt) in _fileToExt)
        {
            if (fileExt == ".cs") continue; // C# 已由 Roslyn 处理
            if (ext != null && !fileExt.Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (_clients.TryGetValue(fileExt, out var client))
            {
                foreach (var d in client.Diagnostics)
                    results.Add((filePath, d));
            }
        }

        return results;
    }

    /// <summary>Group diagnostics by severity for agent context injection.</summary>
    public string FormatDiagnostics(string? extFilter = null)
    {
        var diags = GetDiagnostics(extFilter);
        if (diags.Count == 0) return "";

        var errors = diags.Where(d => d.diag.IsError).ToList();
        var warnings = diags.Where(d => d.diag.IsWarning).ToList();
        var infos = diags.Where(d => !d.diag.IsError && !d.diag.IsWarning).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## LSP Diagnostics");
        sb.AppendLine();

        if (errors.Count > 0)
        {
            sb.AppendLine($"### Errors ({errors.Count})");
            foreach (var (f, d) in errors)
                sb.AppendLine($"  L{d.Line + 1}:{d.Col + 1} | {f} | {d.Message}");
            sb.AppendLine();
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine($"### Warnings ({warnings.Count})");
            foreach (var (f, d) in warnings)
                sb.AppendLine($"  L{d.Line + 1}:{d.Col + 1} | {f} | {d.Message}");
            sb.AppendLine();
        }
        if (infos.Count > 0)
        {
            sb.AppendLine($"### Info/Hints ({infos.Count})");
            foreach (var (f, d) in infos)
                sb.AppendLine($"  L{d.Line + 1} | {f} | {d.Message}");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
        _fileToExt.Clear();
        _csharpDiag?.Dispose();
    }

    private void CleanupIdle()
    {
        // 清理超过闲置超时的 LSP 客户端（但保留 Roslyn 诊断）
        var now = DateTime.UtcNow;
        var staleExts = new List<string>();

        lock (_clients)
        {
            foreach (var (ext, client) in _clients)
            {
                // LspClient 没有 LastActivity 跟踪，此处只做最小清理
                // 完整清理在 Dispose 时进行
            }
        }
    }
}
