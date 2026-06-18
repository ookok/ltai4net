using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Core.Session;
using Microsoft.Agents.AI;

namespace LTAI.TUI;

/// <summary>Interface for UI operations needed by StreamHandler.
/// Enables unit testing without Terminal.Gui dependencies.</summary>
public interface IStreamUI
{
    void ShowSpinner(bool visible);
    void SetStatusText(string text);
    string GetConvEntry(int index);
    void SetConvEntry(int index, string value);
    void AddModifiedFile(string path);
    void RefreshStats();
}

/// <summary>Interface for async streaming operations (ChatAgent wrapper).</summary>
public interface IStreamSource
{
    IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(string input, ISessionHandle? handle, CancellationToken ct);
    Task SaveSessionAsync(CancellationToken ct);
}

/// <summary>Extracted streaming logic from MainWindow.
/// Handles the streaming loop, throttled UI updates, tool tracking, and error handling.
/// Fully testable via IStreamUI and IStreamSource interfaces.</summary>
public sealed class StreamHandler
{
    private readonly IStreamUI _ui;
    private readonly IStreamSource _source;
    private readonly SessionManager _sessionMgr;
    private readonly StringBuilder _markdownCache;
    private readonly List<string> _conv;
    private int _aiMsgCachePos;
    private string _streamBuffer = "";
    private long _lastUIUpdate;
    private const int UI_THROTTLE_MS = 50;
    private static readonly Regex s_toolFileRegex = new(
        @"(?:\u7f16\u8f91|\u5199\u5165|\u521b\u5efa)\s+`?([^\s`]+\.\w+)`?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string StreamBuffer => _streamBuffer;

    public StreamHandler(
        IStreamUI ui,
        IStreamSource source,
        SessionManager sessionMgr,
        StringBuilder markdownCache,
        List<string> conv,
        List<string> modifiedFiles)
    {
        _ui = ui;
        _source = source;
        _sessionMgr = sessionMgr;
        _markdownCache = markdownCache;
        _conv = conv;
    }

    public async Task StreamAsync(string input, CancellationToken ct)
    {
        var handle = _sessionMgr.CurrentHandle;
        _streamBuffer = "";
        _ui.ShowSpinner(true);
        _ui.SetStatusText("\u601d\u8003\u4e2d...");
        _ui.RefreshStats();
        _conv.Add("**AI:** ");
        if (_markdownCache.Length > 0)
            _markdownCache.Append("\n\n");
        _aiMsgCachePos = _markdownCache.Length;
        _markdownCache.Append("**AI:** ");
        var tokenCount = 0;
        try
        {
            await foreach (var u in _source.ChatStreamingAsync(input, handle, ct))
            {
                if (ct.IsCancellationRequested) break;
                var t = u.Text ?? ""; if (t.Length == 0) continue;
                _streamBuffer += t;
                tokenCount++;

                // Track modified files
                if ((t.Contains("\u6b63\u5728\u8c03\u7528") || t.Contains("calling")) &&
                    (t.Contains("Edit") || t.Contains("Write") || t.Contains("Create")))
                {
                    var fileMatch = s_toolFileRegex.Match(_streamBuffer);
                    if (fileMatch.Success)
                    {
                        var filePath = fileMatch.Groups[1].Value;
                        _ui.AddModifiedFile(filePath);
                    }
                }

                var isToolMsg = t.Contains("\u6b63\u5728\u8c03\u7528") || t.Contains("calling") || t.Contains("\u8fd4\u56de");

                // Throttled UI update
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _lastUIUpdate) * 1000.0 / Stopwatch.Frequency;
                if (elapsed >= UI_THROTTLE_MS || tokenCount % 3 == 0)
                {
                    _lastUIUpdate = now;
                    if (isToolMsg)
                    {
                        var trimmed = _streamBuffer.TrimEnd();
                        var lastNewline = trimmed.LastIndexOf('\n');
                        var statusLine = lastNewline >= 0 ? trimmed[(lastNewline + 1)..] : trimmed;
                        _ui.SetStatusText(statusLine);
                    }
                    else
                    {
                        _ui.SetStatusText("\u601d\u8003\u4e2d...");
                    }
                    if (_conv.Count > 0)
                    {
                        _conv[^1] = $"**AI:** {_streamBuffer}\u258a";
                        if (_aiMsgCachePos >= 0)
                        {
                            _markdownCache.Length = _aiMsgCachePos;
                            _markdownCache.Append($"**AI:** {_streamBuffer}\u258a");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected cancellation
        }
        catch
        {
            _aiMsgCachePos = -1;
            _ui.SetStatusText("\u9519\u8bef");
        }
        finally
        {
            var cancelled = ct.IsCancellationRequested;
            _ui.ShowSpinner(false);
            if (_conv.Count > 0 && !cancelled)
            {
                _conv[^1] = $"**AI:** {_streamBuffer}";
                if (_aiMsgCachePos >= 0)
                {
                    _markdownCache.Length = _aiMsgCachePos;
                    _markdownCache.Append($"**AI:** {_streamBuffer}");
                }
            }
            _ui.SetStatusText(cancelled ? "\u5df2\u53d6\u6d88" : "\u5c31\u7eea");
            _ui.RefreshStats();
            if (!cancelled)
                await _source.SaveSessionAsync(ct);
        }
    }
}
