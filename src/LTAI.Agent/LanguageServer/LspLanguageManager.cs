using System.Collections.Concurrent;

namespace LTAI.Agent.LanguageServer;

/// <summary>
/// Maps file extensions to LSP server commands and manages lifecycle
/// for all running LSP processes.
/// </summary>
public sealed class LspLanguageManager : IDisposable
{
    private readonly ConcurrentDictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _fileToExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _rootUri;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(5);
    private readonly Timer? _cleanupTimer;

    private static readonly Dictionary<string, (string cmd, string args)> ExtToLsp = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mbt"] = ("moonbit", "lsp"),
        [".mojo"] = ("mojo", "lsp"),
        ["🔥"] = ("mojo", "lsp"),
        [".cj"] = ("cjc", "lsp"),
    };

    public LspLanguageManager(string? rootPath = null)
    {
        _rootUri = new Uri(rootPath ?? Directory.GetCurrentDirectory()).AbsoluteUri;
        _cleanupTimer = new Timer(_ => CleanupIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>List extensions that have LSP support.</summary>
    public static IEnumerable<string> SupportedExtensions => ExtToLsp.Keys;

    /// <summary>Whether the given file extension has an LSP server.</summary>
    public static bool HasLsp(string ext) => ExtToLsp.ContainsKey(ext);

    /// <summary>Open a file in its LSP — starts server on demand.</summary>
    public async Task OpenFileAsync(string filePath, string content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        if (ext == null || !ExtToLsp.TryGetValue(ext, out var lspInfo)) return;

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

    /// <summary>Notify LSP of file changes.</summary>
    public async Task UpdateFileAsync(string filePath, string content, CancellationToken ct = default)
    {
        if (_fileToExt.TryGetValue(filePath, out var ext) &&
            _clients.TryGetValue(ext, out var client) && client.IsRunning)
        {
            await client.DidChangeAsync(filePath, content, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Get current diagnostics for all open files of a given extension.</summary>
    public List<(string filePath, LspDiagnostic diag)> GetDiagnostics(string? ext = null)
    {
        var results = new List<(string, LspDiagnostic)>();
        foreach (var (filePath, fileExt) in _fileToExt)
        {
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
    }

    private void CleanupIdle()
    {
        // Keep alive for now — cleanup on dispose only
    }
}
