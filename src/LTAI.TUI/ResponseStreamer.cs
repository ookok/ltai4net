using System.Diagnostics;
using System.Text;
using LTAI.Agent;
using LTAI.Core.Configuration;
using LTAI.Core.Rendering;
using LTAI.Core.Session;
using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;

namespace LTAI.TUI;

public interface IStreamerHost
{
    void UpdateFooter(string pickerText, string statusText);
    void UpdateMessages(string streamingContent);
    void ThrottledRefresh();
    void InvalidateRendered();
    void TrimHistory();
    void AutoCompact();
    Task SaveSessionAsync();
    (bool found, bool success, string output, string error) TryParseToolResult(string text);
    (string title, string message, string extraInfo)? TryParseConfirmRequest(string text);
    QuestionPost? GetPendingQuestion();
    Task? ExtractMemory(string userInput);
}

public sealed class ResponseStreamer
{
    private readonly ChatAgent _chat;
    private readonly IChatRenderer _renderer;
    private readonly SessionManager _sessions;
    private readonly QuestionService _questionService;
    private readonly List<(string role, Spectre.Console.Rendering.IRenderable? rendered, string rawContent, string? reasoning)> _history;
    private readonly List<(string name, string args, string result)> _toolCalls;
    private readonly Func<QuestionPost, CancellationToken, Task>? _onAskQuestions;

    private volatile QuestionPost? _pendingQuestion;
    private int _sharedFrameIdx;
    private string _statusText = "";
    private readonly Stopwatch _toolTimer = new();

    public ResponseStreamer(
        ChatAgent chat,
        IChatRenderer renderer,
        SessionManager sessions,
        QuestionService questionService,
        List<(string role, Spectre.Console.Rendering.IRenderable? rendered, string rawContent, string? reasoning)> history,
        List<(string name, string args, string result)> toolCalls,
        Func<QuestionPost, CancellationToken, Task>? onAskQuestions = null)
    {
        _chat = chat;
        _renderer = renderer;
        _sessions = sessions;
        _questionService = questionService;
        _history = history;
        _toolCalls = toolCalls;
        _onAskQuestions = onAskQuestions;
    }

    public async Task StreamAsync(string input, CancellationTokenSource cts)
    {
        var content = new StringBuilder();
        _sharedFrameIdx = 0;
        _statusText = "";
        _pendingQuestion = null;

        content.AppendLine("━━━ 思考中 ━━━");
        _toolCalls.Clear();

        void OnQuestionPosted(QuestionPost post) => _pendingQuestion = post;
        _questionService.QuestionPosted += OnQuestionPosted;
        try
        {

        var ctxRatio = UsageTracker.ContextRatio();
        if (ctxRatio > 0.85)
        {
            var ctxPct = (ctxRatio * 100).ToString("F0");
            _history.Add(("cmd", null, $"[dim]📐 上下文已使用 {ctxPct}%，自动压缩中...[/]", null));
            _renderer.InvalidateRender();
            _renderer.AutoCompact();
        }
        else if (ctxRatio > 0.75)
        {
            var ctxPct = (ctxRatio * 100).ToString("F0");
            _history.Add(("cmd", null, $"[dim]📐 上下文已使用 {ctxPct}%[/]", null));
            _renderer.InvalidateRender();
        }

        _toolTimer.Restart();
        _renderer.UpdateProgress("", "思考中...", null);
        _renderer.InvalidateRender();

        using var spinCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var spinTask = RunSpinAnimation(spinCts);

        try
        {
            var sessionHandle = _sessions.CurrentHandle;
            await foreach (var update in _chat.ChatStreamingAsync(input, sessionHandle).WithCancellation(cts.Token).ConfigureAwait(false))
            {
                if (cts.Token.IsCancellationRequested) break;

                var token = update.Text ?? "";
                if (string.IsNullOrEmpty(token))
                {
                    if (update.Contents?.Count > 0)
                        await ProcessContentsAsync(update.Contents, content, cts).ConfigureAwait(false);
                    continue;
                }

                if (TryParseToolResultToken(token, content))
                    continue;

                if (token.StartsWith("HANDOFF TO "))
                {
                    content.AppendLine($"→ {token}"); _statusText = $"→ {token}";
                    RefreshAfterUpdate(content);
                    continue;
                }
                if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                {
                    content.AppendLine(token); _statusText = token;
                    RefreshAfterUpdate(content);
                    continue;
                }

                content.Append(token);
                RefreshAfterUpdate(content);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            content.AppendLine($"\n⚠ 流式响应错误: {ex.Message}");
        }

        spinCts.Cancel();
        try { await spinTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        _history.Add(("assistant", null, content.ToString(), null));
        _toolCalls.Clear();
        _renderer.TrimHistory();
        await _renderer.SaveSessionAsync().ConfigureAwait(false);

        _ = _renderer.ExtractMemoryAsync(input);
    }
        finally { _questionService.QuestionPosted -= OnQuestionPosted; }
    }

    private Task RunSpinAnimation(CancellationTokenSource spinCts)
    {
        var pulseFrames = new[] { "◐", "◓", "◑", "◒" };
        return Task.Run(async () =>
        {
            try
            {
                while (!spinCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, spinCts.Token).ConfigureAwait(false);
                    var idx = Interlocked.Increment(ref _sharedFrameIdx);
                    var pulse = pulseFrames[idx % pulseFrames.Length];
                    var elapsed = _toolTimer.Elapsed;
                    var timeStr = elapsed.TotalSeconds >= 60
                        ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s"
                        : $"{elapsed.TotalSeconds:F1}s";
                    var line = $"{pulse} 思考中... [{timeStr}]";
                    if (!string.IsNullOrEmpty(_statusText))
                        line += $"  {_statusText}";
                    _renderer.UpdateProgress(pulse, line, timeStr);
                    _renderer.InvalidateRender();
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, spinCts.Token);
    }

    private async Task ProcessContentsAsync(IList<AIContent> contents, StringBuilder content, CancellationTokenSource cts)
    {
        foreach (var c in contents)
        {
            if (c is FunctionCallContent fc)
            {
                if (string.Equals(fc.Name, "AskQuestions", StringComparison.Ordinal))
                {
                    var qp = Interlocked.Exchange(ref _pendingQuestion, null);
                    if (qp != null && _onAskQuestions != null)
                        await _onAskQuestions(qp, cts.Token).ConfigureAwait(false);
                }
                var n = fc.Name ?? "";
                var a = fc.Arguments is Dictionary<string, object?> args
                    ? string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "";
                _toolCalls.Add((n, a, ""));
                var elapsedStr = FormatElapsed(_toolTimer.Elapsed);
                _statusText = $"🛠 {n}({Truncate(a, 30)}) [{elapsedStr}]";
                _renderer.OnToolCall(n, a);
            }
            if (c is FunctionResultContent frc)
            {
                var resultStr = frc.Result?.ToString() ?? "";
                var confirmInfo = _renderer.TryParseConfirmRequest(resultStr);
                if (confirmInfo != null)
                {
                    var choice = await _renderer.ShowConfirmAsync(
                        confirmInfo.Value.Title, confirmInfo.Value.Message,
                        resultStr, confirmInfo.Value.ExtraInfo).ConfigureAwait(false);
                    switch (choice)
                    {
                        case ConfirmChoice.Always:
                            content.AppendLine("  ✅ 已确认（本次会话始终允许）");
                            _statusText = "✅ 已授权 (Always)";
                            break;
                        case ConfirmChoice.Yes:
                            content.AppendLine("  ✅ 已确认");
                            _statusText = "✅ 已确认";
                            break;
                        case ConfirmChoice.No:
                            content.AppendLine("  ⛔ 已拒绝");
                            _statusText = "⛔ 已拒绝";
                            break;
                    }
                    continue;
                }
                var displayResult = resultStr;
                if (displayResult.Length > 300)
                    displayResult = displayResult[..300] + "...";
                if (_toolCalls.Count > 0)
                    _toolCalls[^1] = (_toolCalls[^1].name, _toolCalls[^1].args, displayResult);
                _renderer.OnToolResult(
                    _toolCalls.Count > 0 ? _toolCalls[^1].name : "",
                    resultStr, success: true);
            }
        }
        RefreshAfterUpdate(content);
    }

    private bool TryParseToolResultToken(string token, StringBuilder content)
    {
        var parsed = _renderer.TryParseToolResult(token);
        if (!parsed.Found) return false;

        var subMatch = System.Text.RegularExpressions.Regex.Match(token,
            @"\""type\"":\s*\""(\w+)\"".*\""spawnCount\"":\s*(\d+).*\""elapsedMs\"":\s*(\d+)");
        if (subMatch.Success)
        {
            var st = subMatch.Groups[1].Value;
            var sc = subMatch.Groups[2].Value;
            var ms = int.Parse(subMatch.Groups[3].Value);
            var timeStr = ms >= 1000 ? $"{ms / 1000}.{(ms % 1000) / 100}s" : $"{ms}ms";
            var preview = Truncate(parsed.Output.Replace("\n", " "), 50);
            content.AppendLine($"🔧 子任务 #{sc} ({st}) [{timeStr}] — {preview}");
            _statusText = $"子任务 #{sc} 完成 ({timeStr})";
        }
        else
        {
            var msg = parsed.Success
                ? $"✅ {Truncate(parsed.Output, 60)}"
                : $"❌ {parsed.Error}";
            content.AppendLine(msg);
            _statusText = msg;
        }
        RefreshAfterUpdate(content);
        return true;
    }

    private void RefreshAfterUpdate(StringBuilder content)
    {
        _renderer.OnTextDelta(content.ToString());
        _renderer.UpdateProgress("", $"思考中...  {_statusText}", null);
        _renderer.RequestRender();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.TotalSeconds:F1}s";
}
