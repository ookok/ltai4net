using System.Diagnostics;
using System.Text;
using LTAI.Agent;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Agent.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;
using Microsoft.Extensions.AI;

namespace LTAI.TUI;

public interface IStreamerHost
{
    void UpdateFooter(string pickerText, string statusText);
    void UpdateMessages(string streamingContent);
    void ThrottledRefresh();
    void InvalidateRendered();
    void TrimHistory();
    Task SaveSessionAsync();
    (bool found, bool success, string output, string error) TryParseToolResult(string text);
    (string title, string message, string extraInfo)? TryParseConfirmRequest(string text);
    QuestionPost? GetPendingQuestion();
    Task? ExtractMemory(string userInput);
}

public sealed class ResponseStreamer
{
    private readonly ChatAgent _chat;
    private readonly Rendering.ChatRenderer _renderer;
    private readonly SessionManager _sessions;
    private readonly Layout _layout;
    private readonly LiveDisplayContext _liveCtx;
    private readonly QuestionService _questionService;
    private readonly List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> _history;
    private readonly List<(string name, string args, string result)> _toolCalls;
    private readonly IStreamerHost _host;

    private int _sharedFrameIdx;
    private string _statusText = "";
    private readonly Stopwatch _toolTimer = new();

    public ResponseStreamer(
        ChatAgent chat,
        Rendering.ChatRenderer renderer,
        SessionManager sessions,
        Layout layout,
        LiveDisplayContext liveCtx,
        QuestionService questionService,
        List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> history,
        List<(string name, string args, string result)> toolCalls,
        IStreamerHost host)
    {
        _chat = chat;
        _renderer = renderer;
        _sessions = sessions;
        _layout = layout;
        _liveCtx = liveCtx;
        _questionService = questionService;
        _history = history;
        _toolCalls = toolCalls;
        _host = host;
    }

    public async Task StreamAsync(string input, CancellationTokenSource cts)
    {
        var content = new StringBuilder();
        int toolCallCount = 0;
        _sharedFrameIdx = 0;
        _statusText = "";

        content.AppendLine("━━━ 思考中 ━━━");
        _toolCalls.Clear();

        if (UsageTracker.ContextRatio() > 0.75)
        {
            var ctxPct = (UsageTracker.ContextRatio() * 100).ToString("F0");
            _history.Add(("cmd", null, $"[dim]📐 上下文已使用 {ctxPct}%，自动压缩中...[/]", null));
            _host.InvalidateRendered();
        }

        _toolTimer.Restart();
        _host.UpdateFooter("", $"[deepskyblue1]{Rendering.ChatRenderer.PulseFrames[0]} 思考中...[/]");
        _liveCtx.Refresh();

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
                        await ProcessContentsAsync(update.Contents, content, toolCallCount, cts).ConfigureAwait(false);
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
                    var safeToken = token.Replace("[", "\\[").Replace("]", "\\]");
                    content.AppendLine(safeToken); _statusText = token;
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
            content.AppendLine($"\n[red]⚠ 流式响应错误: {ex.Message.EscapeMarkup()}[/]");
        }

        spinCts.Cancel();
        try { await spinTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        var reasoning = _renderer.RenderToolCallsAsTree(_toolCalls);
        _history.Add(("assistant", null, content.ToString(), reasoning));
        _toolCalls.Clear();
        _host.TrimHistory();
        await _host.SaveSessionAsync().ConfigureAwait(false);

        if (_host.ExtractMemory(input) is Task memTask)
            _ = memTask.ConfigureAwait(false);
    }

    private Task RunSpinAnimation(CancellationTokenSource spinCts)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!spinCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, spinCts.Token).ConfigureAwait(false);
                    var idx = Interlocked.Increment(ref _sharedFrameIdx);
                    var pulse = Rendering.ChatRenderer.PulseFrames[idx % Rendering.ChatRenderer.PulseFrames.Length];
                    var elapsed = _toolTimer.Elapsed;
                    var timeStr = elapsed.TotalSeconds >= 60
                        ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s"
                        : $"{elapsed.TotalSeconds:F1}s";
                    var line = $"{pulse} 思考中... [{timeStr}]";
                    if (!string.IsNullOrEmpty(_statusText))
                        line += $"  {_statusText}";
                    lock (_layout)
                    {
                        _host.UpdateFooter("", $"[deepskyblue1]{line}[/]");
                        _liveCtx.Refresh();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, spinCts.Token);
    }

    private async Task ProcessContentsAsync(IList<AIContent> contents, StringBuilder content, int toolCallCount, CancellationTokenSource cts)
    {
        foreach (var c in contents)
        {
            if (c is FunctionCallContent fc)
            {
                if (string.Equals(fc.Name, "AskQuestions", StringComparison.Ordinal))
                {
                    var qp = _host.GetPendingQuestion();
                    if (qp != null)
                    {
                        var qf = new QuestionFormView(_layout, _liveCtx, _questionService, (p, s) => _host.UpdateFooter(p, s));
                        await qf.ShowAsync(qp, cts.Token).ConfigureAwait(false);
                    }
                }
                toolCallCount++;
                var n = fc.Name ?? "";
                var a = fc.Arguments is Dictionary<string, object?> args
                    ? string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "";
                _toolCalls.Add((n, a, ""));

                var elapsedStr = FormatElapsed(_toolTimer.Elapsed);
                _statusText = $"🛠 {n}({Truncate(a, 30)}) [{elapsedStr}]";
            }
            if (c is FunctionResultContent frc)
            {
                var resultStr = frc.Result?.ToString() ?? "";
                var confirmInfo = _host.TryParseConfirmRequest(resultStr);
                if (confirmInfo != null)
                {
                    var (title, message, extraInfo) = confirmInfo.Value;
                    var choice = await ConfirmationModal.ShowInlineAsync(_layout, _liveCtx, title, message, resultStr, extraInfo).ConfigureAwait(false);
                    switch (choice)
                    {
                        case ConfirmChoice.Always:
                            content.AppendLine($"  ✅ [bold]已确认（本次会话始终允许）[/]");
                            _statusText = "✅ 已授权 (Always)";
                            break;
                        case ConfirmChoice.Yes:
                            content.AppendLine($"  ✅ [bold]已确认[/]");
                            _statusText = "✅ 已确认";
                            break;
                        case ConfirmChoice.No:
                            content.AppendLine($"  ⛔ [bold red]已拒绝[/]");
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
            }
        }
        RefreshAfterUpdate(content);
    }

    private bool TryParseToolResultToken(string token, StringBuilder content)
    {
        var parsed = _host.TryParseToolResult(token);
        if (!parsed.found) return false;

        var subMatch = System.Text.RegularExpressions.Regex.Match(token, @"\""type\"":\s*\""(\w+)\"".*\""spawnCount\"":\s*(\d+).*\""elapsedMs\"":\s*(\d+)");
        if (subMatch.Success)
        {
            var st = subMatch.Groups[1].Value;
            var sc = subMatch.Groups[2].Value;
            var ms = int.Parse(subMatch.Groups[3].Value);
            var timeStr = ms >= 1000 ? $"{ms / 1000}.{(ms % 1000) / 100}s" : $"{ms}ms";
            var preview = Truncate(parsed.output.Replace("\n", " "), 50);
            content.AppendLine($"🔧 [bold]子任务 #{sc} ({st})[/] [grey]{timeStr}[/] — {preview.EscapeMarkup()}");
            _statusText = $"子任务 #{sc} 完成 ({timeStr})";
        }
        else
        {
            var msg = parsed.success
                ? $"✅ {Truncate(parsed.output, 60)}"
                : $"❌ {parsed.error.EscapeMarkup()}";
            content.AppendLine(msg);
            _statusText = msg;
        }
        RefreshAfterUpdate(content);
        return true;
    }

    private void RefreshAfterUpdate(StringBuilder content)
    {
        lock (_layout)
        {
            _host.UpdateMessages(content.ToString());
            _host.UpdateFooter("", $"{Rendering.ChatRenderer.PulseFrames[_sharedFrameIdx % Rendering.ChatRenderer.PulseFrames.Length]} 处理中...  {_statusText}");
        }
        _host.ThrottledRefresh();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.TotalSeconds:F1}s";
}
