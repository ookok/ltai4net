using System.Text;
using LTAI.Core.Session;
using Microsoft.Agents.AI;

namespace LTAI.Desktop;

/// <summary>Result of processing a single streaming response.</summary>
public sealed record StreamProcessResult(
    string FullResponse,
    string? ThinkingText,
    IReadOnlyList<string> ToolTokens,
    bool Cancelled);

/// <summary>Options for the response text rendering callback.</summary>
public sealed record ResponseTextOptions(string Text, string LastRenderedText);

/// <summary>Interface for UI callbacks during streaming. Enables unit testing
/// without creating Avalonia controls.</summary>
public interface IStreamUiCallbacks
{
    void OnThinkingStart();
    void OnThinkingUpdate(string text);
    void OnToolToken(string token);
    void OnFirstToken();
    void OnResponseChunk(string text, string lastRenderedText);
    void OnComplete();
    void OnCancelled();
    void OnError(string message);
}

/// <summary>Pure streaming processor: parses AgentResponseUpdate tokens,
/// extracts thinking tags, tracks tool calls, and accumulates the final response.
/// No Avalonia dependencies — fully testable with mocks.</summary>
public sealed class ChatStreamProcessor
{
    private readonly IStreamSource _source;
    private readonly IStreamUiCallbacks _ui;
    private readonly StringBuilder _responseBuf = new();
    private readonly StringBuilder _thinkBuf = new();
    private readonly List<string> _toolTokens = new();
    private bool _inThinking;
    private bool _firstTokenReceived;
    private string _lastRenderedText = "";
    private int _tokenCount;

    public string FullResponse => _responseBuf.ToString();
    public string? ThinkingText => _thinkBuf.Length > 0 ? _thinkBuf.ToString() : null;
    public IReadOnlyList<string> ToolTokens => _toolTokens;
    public bool HasPlan => FullResponse.Contains("## Plan:") || FullResponse.Contains("approve");

    public ChatStreamProcessor(IStreamSource source, IStreamUiCallbacks ui)
    {
        _source = source;
        _ui = ui;
    }

    public async Task<StreamProcessResult> ProcessAsync(
        string query,
        ISessionHandle? sessionHandle,
        CancellationToken ct)
    {
        _responseBuf.Clear();
        _thinkBuf.Clear();
        _toolTokens.Clear();
        _inThinking = false;
        _firstTokenReceived = false;
        _lastRenderedText = "";
        _tokenCount = 0;

        try
        {
            await foreach (var update in _source.ChatStreamingAsync(query, sessionHandle, ct))
            {
                var token = update.Text ?? "";
                if (token.Length == 0) continue;
                _tokenCount++;

                // Check tool result renderer
                var rendered = _source.RenderTool(token);
                if (rendered != null)
                {
                    _responseBuf.Append($" {Truncate(token, 80)}");
                    _toolTokens.Add(token);
                    _ui.OnToolToken(token);
                    continue;
                }

                // Thinking tags
                if (token.StartsWith("<thinking>"))
                {
                    _inThinking = true;
                    _thinkBuf.Append(token.AsSpan("<thinking>".Length));
                    _ui.OnThinkingStart();
                }
                else if (token.EndsWith("</thinking>"))
                {
                    _thinkBuf.Append(token.AsSpan(0, token.Length - "</thinking>".Length));
                    _inThinking = false;
                    _ui.OnThinkingUpdate(_thinkBuf.ToString());
                }
                else if (_inThinking)
                {
                    _thinkBuf.Append(token);
                    _ui.OnThinkingUpdate(_thinkBuf.ToString());
                }
                else
                {
                    if (!_firstTokenReceived)
                    {
                        _firstTokenReceived = true;
                        _ui.OnFirstToken();
                    }
                    _responseBuf.Append(token);

                    if (_tokenCount % 8 == 0)
                    {
                        var text = _responseBuf.ToString();
                        if (text != _lastRenderedText && !HasUnclosedFence(text))
                        {
                            _lastRenderedText = text;
                            _ui.OnResponseChunk(text, _lastRenderedText);
                        }
                    }
                }

                if (_tokenCount % 20 == 0)
                    await Task.Yield();
            }

            // Final render
            var finalText = _responseBuf.ToString();
            if (finalText != _lastRenderedText)
                _ui.OnComplete();

            return new StreamProcessResult(
                FullResponse: finalText,
                ThinkingText: _thinkBuf.Length > 0 ? _thinkBuf.ToString() : null,
                ToolTokens: _toolTokens.AsReadOnly(),
                Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            _responseBuf.Append(" [cancelled]");
            _ui.OnCancelled();
            return new StreamProcessResult(
                FullResponse: _responseBuf.ToString(),
                ThinkingText: _thinkBuf.Length > 0 ? _thinkBuf.ToString() : null,
                ToolTokens: _toolTokens.AsReadOnly(),
                Cancelled: true);
        }
        catch (Exception ex)
        {
            _responseBuf.Append($"\n[Error] {ex.Message}");
            _ui.OnError(ex.Message);
            return new StreamProcessResult(
                FullResponse: _responseBuf.ToString(),
                ThinkingText: _thinkBuf.Length > 0 ? _thinkBuf.ToString() : null,
                ToolTokens: _toolTokens.AsReadOnly(),
                Cancelled: false);
        }
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";

    /// <summary>Check if text has unclosed code fences.</summary>
    internal static bool HasUnclosedFence(string text)
    {
        var count = 0;
        int idx = 0;
        while ((idx = text.IndexOf("```", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += 3;
        }
        return count % 2 != 0;
    }
}

/// <summary>Abstraction over the streaming source (ChatAgent or mock).</summary>
public interface IStreamSource
{
    IAsyncEnumerable<Microsoft.Agents.AI.AgentResponseUpdate> ChatStreamingAsync(
        string query, ISessionHandle? sessionHandle, CancellationToken ct);
    object? RenderTool(string token);
}
