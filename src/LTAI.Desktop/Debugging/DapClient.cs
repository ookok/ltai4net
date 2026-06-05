using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LTAI.Desktop.Debugging;

public sealed class DapClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly StreamReader _stdout;
    private int _seq;
    private readonly Task _readerTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = new();

    public event Action<string, JsonObject?>? EventReceived;
    public event Action<string>? OutputReceived;
    public event Action? Disconnected;

    public DapClient(string command, string[] args, string workingDir)
    {
        var psi = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _process = new Process { StartInfo = psi };
        _process.Start();

        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputReceived?.Invoke(e.Data);
        };
        _process.BeginErrorReadLine();

        _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public async Task<JsonObject> CallAsync(string command, JsonObject? args = null)
    {
        var id = Interlocked.Increment(ref _seq);
        var req = new JsonObject
        {
            ["seq"] = id,
            ["type"] = "request",
            ["command"] = command,
        };
        if (args != null) req["arguments"] = args;

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        await SendAsync(req);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task SendAsync(JsonObject msg)
    {
        var json = msg.ToJsonString() + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        await _stdin.WriteAsync(Encoding.UTF8.GetBytes(header));
        await _stdin.WriteAsync(bytes);
        await _stdin.FlushAsync();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReadMessageAsync(ct);
                if (msg == null) break;

                var type = msg["type"]?.GetValue<string>();
                if (type == "response")
                {
                    var reqSeq = msg["request_seq"]?.GetValue<long>() ?? 0;
                    TaskCompletionSource<JsonObject>? tcs;
                    lock (_pending) _pending.TryGetValue((int)reqSeq, out tcs);
                    if (tcs != null)
                    {
                        if (msg.TryGetPropertyValue("success", out var s) && s?.GetValue<bool>() == false)
                        {
                            var err = msg["message"]?.GetValue<string>() ?? "unknown error";
                            tcs.TrySetException(new InvalidOperationException($"DAP error: {err}"));
                        }
                        else
                        {
                            tcs.TrySetResult(msg);
                        }
                        lock (_pending) _pending.Remove((int)reqSeq);
                    }
                }
                else if (type == "event")
                {
                    var evt = msg["event"]?.GetValue<string>();
                    var body = msg["body"] as JsonObject;
                    EventReceived?.Invoke(evt!, body);
                }
                else if (type == "error")
                {
                    OutputReceived?.Invoke(msg["message"]?.GetValue<string>() ?? "DAP error");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OutputReceived?.Invoke($"DAP reader: {ex.Message}"); }
        finally { Disconnected?.Invoke(); }
    }

    private async Task<JsonObject?> ReadMessageAsync(CancellationToken ct)
    {
        var contentLen = 0;
        while (true)
        {
            var line = await _stdout.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return null;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLen = int.Parse(line.AsSpan("Content-Length: ".Length));
            }
            else if (string.IsNullOrEmpty(line) && contentLen > 0)
            {
                break;
            }
        }

        var buffer = new char[contentLen];
        var totalRead = 0;
        while (totalRead < contentLen)
        {
            var read = await _stdout.ReadAsync(buffer.AsMemory(totalRead, contentLen - totalRead), ct).ConfigureAwait(false);
            if (read == 0) return null;
            totalRead += read;
        }

        return JsonNode.Parse(new string(buffer, 0, totalRead)) as JsonObject;
    }

    public async Task DisconnectAsync()
    {
        try
        {
            await CallAsync("disconnect");
        }
        catch { }

        try
        {
            await _cts.CancelAsync();
            await _readerTask;
        }
        catch { }

        _stdin.Close();
        _stdout.Close();
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        _process.Dispose();
    }

    public async ValueTask DisposeAsync()
        => await DisconnectAsync();
}
