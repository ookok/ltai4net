using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.AI;
using LTAI.Agent.Workflows;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

public sealed class ChatAgent
{
    private static readonly HashSet<string> _simpleQueries = new(StringComparer.OrdinalIgnoreCase)
    {
        "你好", "hi", "hello", "hey", "嗨",
        "早上好", "下午好", "晚上好", "午安", "晚安",
        "help", "status", "clear", "ping", "test",
        "thanks", "谢谢", "thank you", "thank u", "多谢", "感谢",
        "bye", "再见", "拜拜", "goodbye",
        "yes", "no", "ok", "okay", "好的", "嗯", "哦",
        "who are you", "你是谁",
        "?", "？", "！", "!", "",
    };

    internal static string GetOrCreateTraceId() =>
        Activity.Current?.Id ?? Guid.NewGuid().ToString("n");
    private readonly AIAgent _agent;
    private readonly AIAgent _proAgent;
    private readonly bool _sameModel;
    private readonly AgentWorkflows? _workflows;
    private readonly BudgetTracker? _budgetTracker;
    private readonly LocalEmbedder? _localEmbedder;
    private readonly IHttpClientFactory? _httpFactory;

    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, AgentWorkflows? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null,
        bool sameModel = false)
    {
        _agent = agent;
        _proAgent = proAgent ?? agent;
        _sameModel = sameModel;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _localEmbedder = localEmbedder;
        _httpFactory = httpFactory;
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_localEmbedder?.Available == false)
            _ = _localEmbedder.Dim;

        try
        {
            using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            warmCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var http = _httpFactory?.CreateClient("llm");
            if (http != null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Options, "https://api.deepseek.com/v1/chat/completions");
                _ = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, warmCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch { }
    }

    public async Task<string> ChatAsync(string message, ISessionHandle? sessionHandle = null,
        string? userId = null, CancellationToken ct = default)
    {
        userId ??= "default";
        if (_budgetTracker != null)
        {
            var estimatedTokens = Math.Max(10, message.Length / 4);
            var (allowed, remaining) = _budgetTracker.TryConsume(userId, estimatedTokens);
            if (!allowed)
            {
                return $"⛔ Token budget exceeded. Remaining budget: {remaining} tokens. " +
                       "Please wait for budget reset or contact your administrator.";
            }
        }

        var traceId = GetOrCreateTraceId();
        var session = sessionHandle != null
            ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
            : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var trimmed = message.Trim();
        var isSimple = trimmed.Length <= 10 || _simpleQueries.Contains(trimmed);
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        var r = await _agent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = r.Messages?.LastOrDefault()?.Text ?? "";

        if (isSimple)
        {
            if (sessionHandle != null)
                await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
            return text;
        }

        if (_sameModel)
        {
            if (sessionHandle != null)
                await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
            return text;
        }

        var needsPro = text.Contains("<<<NEEDS_PRO:");

        if (!needsPro && text.Length > 20)
        {
            var lower = text.ToLowerInvariant();
            var failPatterns = new[] { "无法获取", "无法确定", "无法提供", "无法访问",
                "抱歉", "我无法", "暂时无法", "目前还不支持", "我不能",
                "cannot", "can't", "unable to", "don't know", "i don't" };
            if (failPatterns.Any(p => lower.Contains(p)))
            {
                System.Diagnostics.Debug.WriteLine("[ChatAgent] L1→L2 auto-upgrade triggered by refusal pattern");
                needsPro = true;
            }
        }

        if (needsPro)
        {
            var reason = "complex task";
            if (text.Contains("<<<NEEDS_PRO:"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, @"<<<NEEDS_PRO:\s*(.+?)>>>");
                if (match.Success) reason = match.Groups[1].Value.Trim();
            }
            else
            {
                reason = "flash model declined to answer";
            }

            r = await _proAgent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
            text = r.Messages?.LastOrDefault()?.Text ?? "";
            text = $"[Auto-upgraded to Pro: {reason}]\n\n{text}";
        }

        text = await EnforceAndReflectAsync(text, message, session, ct).ConfigureAwait(false);
        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
        return text;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(
        string message, ISessionHandle? sessionHandle = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = sessionHandle != null
            ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
            : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);

        await foreach (var update in _agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, message)], session, cancellationToken: ct).ConfigureAwait(false))
        {
            if (update.Contents is { Count: > 0 })
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent fc when !string.IsNullOrEmpty(fc.Name):
                            LTAI.Core.Configuration.UsageTracker.RecordToolCall();
                            LTAI.Core.Configuration.UsageTracker.SetActiveTool(fc.Name);
                            LTAI.Core.Configuration.UsageTracker.StartToolTimer();
                            yield return new AgentResponseUpdate(ChatRole.Assistant, $"⏳ 正在调用 {fc.Name}...\n");
                            break;
                        case FunctionResultContent frc:
                            LTAI.Core.Configuration.UsageTracker.StopToolTimer();
                            var preview = frc.Result?.ToString() ?? "(null)";
                            if (preview.Length > 200) preview = preview[..200] + "...";
                            yield return new AgentResponseUpdate(ChatRole.Assistant, $"  ✅ 返回: {preview}\n");
                            break;
                    }
                }
            }

            yield return update;
        }
        LTAI.Core.Configuration.UsageTracker.SetActiveTool("");

        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
    }

    public Task<AgentResponse> RunWorkflowAsync(string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult(new AgentResponse(
                new ChatMessage(ChatRole.Assistant, "Workflow orchestrator not available.")));
        return _workflows.RunHandoffAsync(task, traceId: GetOrCreateTraceId(), ct: ct);
    }

    public Task<string> RunSequentialAsync(string[] agentNames, string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult("Workflow orchestrator not available.");
        return _workflows.RunSequentialAsync(agentNames, task, traceId: GetOrCreateTraceId(), ct: ct);
    }

    private async Task<string> EnforceAndReflectAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lower = text.ToLowerInvariant();

        var cantPatterns = new[] { "无法获取", "无法确定", "无法提供", "没有权限", "无法访问",
            "无法直接", "无法知道", "不知道当前", "不知道今天",
            "我不知道", "我不确定", "没有内置", "没有实时",
            "抱歉", "我无法", "暂时无法", "目前还不支持", "请稍后再试",
            "我不能", "我不可以", "对不起", "不支持", "暂不支持",
            "cannot", "can't", "unable to", "don't have", "don't know",
            "no access", "not have access", "not able to", "i don't" };

        if (!cantPatterns.Any(p => lower.Contains(p)) && text.Length >= 15
            && !text.Contains("{{") && !text.Contains("TODO"))
            return text;

        var stage1Prompt = $"""
            你的回答存在以下问题，请修正：
            - 如果问题需要工具，调用工具获取真实数据
            - 不要拒绝、猜测或编造
            - 确保回答完整（不含占位符）

            用户原始问题：{originalMessage}

            你的回复：{text}
            请修正后重新回答。
            """;

        try
        {
            var result1 = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, stage1Prompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var refined1 = result1.Messages?.LastOrDefault()?.Text ?? "";

            if (!string.IsNullOrWhiteSpace(refined1) && refined1.Length > 10)
                return $"[校正]\n\n{refined1}";

            var stage2Prompt = $"你必须使用工具来回答用户问题。不要拒绝、不要猜测。\n\n用户问题是: {originalMessage}";
            var result2 = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, stage2Prompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var refined2 = result2.Messages?.LastOrDefault()?.Text ?? "";

            if (!string.IsNullOrWhiteSpace(refined2) && refined2.Length > 10)
                return $"[工具]\n\n{refined2}";
        }
        catch { }

        return $"无法完成请求。问题超出了当前能力范围：需要获取实时数据但工具调用未成功。请稍后重试或简化您的请求。";
    }

    private async Task<AgentSession> CreateAgentSessionFromHandleAsync(ISessionHandle handle, CancellationToken ct)
    {
        var json = handle.SerializeToJson();
        if (string.IsNullOrEmpty(json))
            return await _agent.CreateSessionAsync(ct).ConfigureAwait(false);

        var element = JsonDocument.Parse(json).RootElement;
        return await _agent.DeserializeSessionAsync(element, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task SaveSessionToHandleAsync(AgentSession session, ISessionHandle handle, CancellationToken ct)
    {
        var json = await _agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
        handle.UpdateFromJson(json.GetRawText());
    }
}
