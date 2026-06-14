using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.LanguageServer;

/// <summary>
/// Lightweight stdio LSP client per language server process.
/// Sends initialize/didOpen/didChange and receives diagnostics notifications.
/// No full LSP protocol compliance — only the subset needed for diagnostics.
/// </summary>
public sealed class LspClient : IDisposable
{
    private readonly string _command;
    private readonly string _args;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _readerTask;
    private readonly CancellationTokenSource _killCts = new();
    private int _msgId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    private readonly object _sendLock = new();

    private readonly List<LspDiagnostic> _diagnostics = [];
    public IReadOnlyList<LspDiagnostic> Diagnostics => _diagnostics;

    public bool IsRunning => _process is { HasExited: false };

    public LspClient(string command, string args = "")
    {
        _command = command;
        _args = args;
    }

    /// <summary>Start the LSP process and send initialize request.</summary>
    public async Task StartAsync(string rootUri, CancellationToken ct = default)
    {
        if (IsRunning) return;

        var psi = new ProcessStartInfo(_command, _args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = new Process { StartInfo = psi };
        _process.Start();

        _stdin = new StreamWriter(_process.StandardInput.BaseStream, Encoding.UTF8);
        _stdout = new StreamReader(_process.StandardOutput.BaseStream, Encoding.UTF8);

        // Background reader: parse response headers + body
        _readerTask = Task.Run(() => ReadLoop(_killCts.Token), _killCts.Token);

        // Initialize
        await SendRequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new { },
        }, ct).ConfigureAwait(false);

        await SendNotificationAsync("initialized", new { }, ct).ConfigureAwait(false);
    }

    /// <summary>Notify LSP that a file was opened.</summary>
    public async Task DidOpenAsync(string filePath, string content, CancellationToken ct = default)
    {
        if (!IsRunning) return;
        var uri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
        await SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = LangFromExt(filePath), version = 1, text = content },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Notify LSP that a file changed.</summary>
    public async Task DidChangeAsync(string filePath, string content, CancellationToken ct = default)
    {
        if (!IsRunning) return;
        var uri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
        await SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = DateTime.UtcNow.Millisecond },
            contentChanges = new[] { new { text = content } },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Request full diagnostics for a file (textDocument/pullDiagnostics if supported).</summary>
    public async Task<List<LspDiagnostic>> RequestDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        if (!IsRunning) return [];
        var uri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
        try
        {
            var result = await SendRequestAsync("textDocument/diagnostic", new
            {
                textDocument = new { uri },
            }, ct).ConfigureAwait(false);

            if (result?.TryGetProperty("kind", out _) == true &&
                result.Value.TryGetProperty("items", out var items))
            {
                return ParseDiagnostics(items);
            }
        }
        catch { }
        return [];
    }

    public void Dispose()
    {
        try { _killCts.Cancel(); } catch { }
        try
        {
            // Best-effort graceful shutdown with 2s timeout
            var shutdownTask = SendNotificationAsync("shutdown", new { }, CancellationToken.None);
            if (!shutdownTask.Wait(TimeSpan.FromSeconds(2)))
            {
                // Force kill on timeout
                try { _process?.Kill(); } catch { }
            }
        }
        catch { try { _process?.Kill(); } catch { } }
        _stdin?.Dispose();
        _stdout?.Dispose();
        _process?.Dispose();
        _killCts.Dispose();
    }

    // ── private ──

    private async Task ReadLoop(CancellationToken ct)
    {
        var buffer = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _stdout != null)
            {
                var line = await _stdout.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;

                // LSP uses HTTP-like headers: Content-Length: N\r\n\r\n<body>
                if (string.IsNullOrEmpty(line))
                {
                    // End of headers — read the body
                    var contentLen = ParseContentLength(buffer.ToString());
                    buffer.Clear();

                    if (contentLen > 0)
                    {
                        var bodyChars = new char[contentLen];
                        var read = 0;
                        while (read < contentLen)
                            read += await _stdout.ReadAsync(bodyChars.AsMemory(read, contentLen - read), ct).ConfigureAwait(false);

                        var body = new string(bodyChars, 0, contentLen);
                        HandleMessage(body);
                    }
                }
                else
                {
                    buffer.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LTAI] LSP read loop terminated: {ex.Message}");
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Response to a request: id field present
            if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var responseId))
            {
                if (_pendingRequests.TryRemove(responseId, out var tcs))
                {
                    if (root.TryGetProperty("result", out var result))
                        tcs.TrySetResult(result.Clone());
                    else if (root.TryGetProperty("error", out var error))
                        tcs.TrySetResult(error.Clone());
                    else
                        tcs.TrySetResult(null);
                }
                return;
            }

            // Notification: method + params
            if (root.TryGetProperty("method", out var method))
            {
                var methodName = method.GetString() ?? "";
                if (methodName == "textDocument/publishDiagnostics" &&
                    root.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("diagnostics", out var diags))
                {
                    lock (_sendLock)
                    {
                        _diagnostics.Clear();
                        _diagnostics.AddRange(ParseDiagnostics(diags));
                    }
                }
            }
        }
        catch { }
    }

    private async Task<JsonElement?> SendRequestAsync(string method, object? paramsObj, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _msgId);
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = paramsObj,
        });
        await SendRawAsync(payload, ct).ConfigureAwait(false);
        // Request-response: wait for the matching response from ReadLoop
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingRequests)
        {
            _pendingRequests[id] = tcs;
        }
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_pendingRequests) { _pendingRequests.TryRemove(id, out _); }
        }
    }

    private async Task SendNotificationAsync(string method, object? paramsObj, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = paramsObj,
        });
        await SendRawAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        if (_stdin == null) return;
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n";
        lock (_sendLock)
        {
            _stdin.Write(header);
            _stdin.Write(json);
            _stdin.Flush();
        }
        await Task.CompletedTask;
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim('\r', ' ');
            if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(trimmed.AsSpan("Content-Length:".Length), out var len))
                return len;
        }
        return 0;
    }

    private static string LangFromExt(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mbt" => "moonbit",
        ".mojo" or "🔥" => "mojo",
        ".cj" => "cangjie",
        ".cs" => "csharp",
        ".py" => "python",
        ".rs" => "rust",
        ".go" => "go",
        ".ts" => "typescript",
        ".js" => "javascript",
        ".java" => "java",
        _ => "",
    };

    private static List<LspDiagnostic> ParseDiagnostics(JsonElement items)
    {
        var list = new List<LspDiagnostic>();
        foreach (var item in items.EnumerateArray())
        {
            var range = item.GetProperty("range");
            var start = range.GetProperty("start");
            list.Add(new LspDiagnostic(
                Severity: item.TryGetProperty("severity", out var s) ? s.GetInt32() : 1,
                Message: item.GetProperty("message").GetString() ?? "",
                Line: start.GetProperty("line").GetInt32(),
                Col: start.GetProperty("character").GetInt32(),
                Code: item.TryGetProperty("code", out var c) ? c.GetString() : null,
                Source: item.TryGetProperty("source", out var src) ? src.GetString() : null
            ));
        }
        return list;
    }
}

public sealed record LspDiagnostic(int Severity, string Message, int Line, int Col, string? Code, string? Source)
{
    /// <summary>1=error, 2=warning, 3=info, 4=hint</summary>
    public bool IsError => Severity <= 1;
    public bool IsWarning => Severity == 2;
}
