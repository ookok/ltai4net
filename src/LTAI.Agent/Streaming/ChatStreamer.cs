using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LTAI.Core.Configuration;
using LTAI.Core.Rendering;
using LTAI.Core.Session;
using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Streaming;

public sealed class ChatStreamer
{
    private readonly ChatAgent _chat;
    private readonly IChatRenderer _renderer;
    private readonly SessionManager _sessions;
    private readonly QuestionService _questionService;
    private readonly List<(string name, string args, string result)> _toolCalls = new();

    private int _frameIdx;
    private string _statusText = "";
    private readonly Stopwatch _timer = new();

    private volatile QuestionPost? _pendingQuestion;

    public ChatStreamer(
        ChatAgent chat,
        IChatRenderer renderer,
        SessionManager sessions,
        QuestionService questionService)
    {
        _chat = chat;
        _renderer = renderer;
        _sessions = sessions;
        _questionService = questionService;
    }

    public Func<QuestionPost, CancellationToken, Task>? OnAskQuestions { get; set; }

    public async Task StreamAsync(string input, CancellationTokenSource cts)
    {
        _frameIdx = 0;
        _statusText = "";
        _pendingQuestion = null;
        _toolCalls.Clear();

        void OnQuestionPosted(QuestionPost post) => _pendingQuestion = post;
        _questionService.QuestionPosted += OnQuestionPosted;
        try
        {
            CheckContextRatio();
            _timer.Restart();
            _renderer.OnStreamStart();

            using var spinCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var spinTask = RunSpinAnimation(spinCts);

            try
            {
                var sessionHandle = _sessions.CurrentHandle;
                await foreach (var update in _chat.ChatStreamingAsync(input, sessionHandle)
                    .WithCancellation(cts.Token).ConfigureAwait(false))
                {
                    if (cts.Token.IsCancellationRequested) break;

                    var token = update.Text ?? "";
                    if (string.IsNullOrEmpty(token))
                    {
                        if (update.Contents?.Count > 0)
                            await ProcessContentsAsync(update.Contents).ConfigureAwait(false);
                        continue;
                    }

                    if (await TryParseToolResultToken(token).ConfigureAwait(false))
                        continue;

                    if (token.StartsWith("HANDOFF TO "))
                    {
                        _statusText = $"→ {token}";
                        await RefreshAsync().ConfigureAwait(false);
                        continue;
                    }
                    if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                    {
                        _statusText = token;
                        await RefreshAsync().ConfigureAwait(false);
                        continue;
                    }

                    await _renderer.OnTextDelta(token).ConfigureAwait(false);
                    await RefreshAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected cancellation
            }
            catch (Exception ex)
            {
                await _renderer.OnTextDelta($"\n⚠ 流式响应错误: {ex.Message}").ConfigureAwait(false);
            }

            spinCts.Cancel();
            try { await spinTask.ConfigureAwait(false); } catch (OperationCanceledException)
            {
                // expected cancellation
            }

            _renderer.OnStreamEnd();
            _toolCalls.Clear();
            _renderer.TrimHistory();
            // Save session only if not cancelled — cancellation may leave partial state
            if (!cts.IsCancellationRequested)
                await _renderer.SaveSessionAsync().ConfigureAwait(false);
            _ = _renderer.ExtractMemoryAsync(input);
        }
        finally { _questionService.QuestionPosted -= OnQuestionPosted; }
    }

    private void CheckContextRatio()
    {
        var ctxRatio = UsageTracker.ContextRatio();
        if (ctxRatio > 0.85)
        {
            _renderer.AutoCompact();
        }
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
                    var idx = Interlocked.Increment(ref _frameIdx);
                    var pulse = pulseFrames[idx % pulseFrames.Length];
                    var elapsed = _timer.Elapsed;
                    var timeStr = elapsed.TotalSeconds >= 60
                        ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s"
                        : $"{elapsed.TotalSeconds:F1}s";
                    var line = $"{pulse} 思考中... [{timeStr}]";
                    if (!string.IsNullOrEmpty(_statusText))
                        line += $"  {_statusText}";
                    await _renderer.UpdateProgress(pulse, line, timeStr).ConfigureAwait(false);
                    _renderer.InvalidateRender();
                }
            }
            catch (OperationCanceledException)
            {
                // expected cancellation
            }
            catch
            {
                // non-critical, best-effort
            }
        }, spinCts.Token);
    }

    private async Task ProcessContentsAsync(IList<AIContent> contents)
    {
        foreach (var c in contents)
        {
            if (c is FunctionCallContent fc)
            {
                if (string.Equals(fc.Name, "AskQuestions", StringComparison.Ordinal))
                {
                    var qp = Interlocked.Exchange(ref _pendingQuestion, null);
                    if (qp != null && OnAskQuestions != null)
                        await OnAskQuestions(qp, CancellationToken.None).ConfigureAwait(false);
                }
                var n = fc.Name ?? "";
                var a = fc.Arguments is Dictionary<string, object?> args
                    ? string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "";
                _toolCalls.Add((n, a, ""));
                _statusText = $"🛠 {n}({Truncate(a, 30)}) [{FormatElapsed(_timer.Elapsed)}]";
                await _renderer.OnToolCall(n, a).ConfigureAwait(false);
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
                        case ConfirmChoice.Always: _statusText = "✅ 已授权 (Always)"; break;
                        case ConfirmChoice.Yes:   _statusText = "✅ 已确认"; break;
                        case ConfirmChoice.No:    _statusText = "⛔ 已拒绝"; break;
                    }
                    continue;
                }
                var displayResult = resultStr;
                if (displayResult.Length > 300)
                    displayResult = displayResult[..300] + "...";
                if (_toolCalls.Count > 0)
                    _toolCalls[^1] = (_toolCalls[^1].name, _toolCalls[^1].args, displayResult);
                await _renderer.OnToolResult(
                    _toolCalls.Count > 0 ? _toolCalls[^1].name : "",
                    resultStr, success: true).ConfigureAwait(false);
            }
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task<bool> TryParseToolResultToken(string token)
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
            _statusText = $"子任务 #{sc} 完成 ({timeStr})";
        }
        else
        {
            _statusText = parsed.Success
                ? $"✅ {Truncate(parsed.Output, 60)}"
                : $"❌ {parsed.Error}";
        }
        await RefreshAsync().ConfigureAwait(false);
        return true;
    }

    private async ValueTask RefreshAsync()
    {
        await _renderer.UpdateProgress("", $"思考中...  {_statusText}", null).ConfigureAwait(false);
        _renderer.RequestRender();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.TotalSeconds:F1}s";
}
