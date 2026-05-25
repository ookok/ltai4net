using System.Collections.Concurrent;
using LTAI.Agent.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.Agent.Intelligence;

public sealed class L1L2Collaboration
{
    private static readonly Lazy<L1L2Collaboration> _instanceLazy = new(() => new L1L2Collaboration());
    public static L1L2Collaboration Instance => _instanceLazy.Value;

    private readonly ConcurrentDictionary<string, string> _worldState = new();
    private Func<string, int, Task<string?>>? _humanCallback;
    private string _l2Feedback = "";
    private readonly ILogger<L1L2Collaboration>? _logger;

    public L1L2Collaboration(ILogger<L1L2Collaboration>? logger = null)
    {
        _logger = logger;
    }

    public async Task<CollaborationResult> CollaborativeChatAsync(
        string userQuery,
        int maxRounds,
        Func<string, int, Task<string?>>? humanCallback,
        IChatClient l2ChatClient,
        IChatClient? l1ChatClient = null,
        string extraContext = "")
    {
        _humanCallback = humanCallback;
        var needs = new List<Need>();
        var insights = new List<string>();
        var totalTokens = 0;
        var totalLatency = 0.0;
        var fullText = new List<string>();

        var l1Functions = BuildL2DelegationFunctions();
        var l1Preload = await L1PreloadAsync(userQuery, l1ChatClient).ConfigureAwait(false);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, BuildDelegationPrompt(l1Preload, extraContext)),
            new(ChatRole.User, userQuery)
        };

        for (var round = 0; round < maxRounds; round++)
        {
            var roundStart = DateTime.UtcNow;
            var chatOptions = new ChatOptions { Tools = l1Functions };

            var response = await l2ChatClient.GetResponseAsync(
                history, chatOptions, CancellationToken.None).ConfigureAwait(false);

            totalTokens += EstimateTokens(response.Text ?? "");

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            var textContent = response.Messages
                .SelectMany(m => m.Contents.OfType<TextContent>())
                .Select(t => t.Text)
                .ToList();

            if (textContent.Count > 0)
                fullText.AddRange(textContent);

            if (functionCalls.Count == 0 && textContent.Count > 0)
            {
                insights.Add("L2 resolved without delegating");
                break;
            }

            if (functionCalls.Count > 0)
            {
                history.Add(new ChatMessage(ChatRole.Assistant,
                    response.Messages.SelectMany(m => m.Contents).ToList()));
            }

            var fulfillTasks = new Dictionary<string, Task<FunctionResultContent>>();
            foreach (var call in functionCalls)
            {
                var need = new Need
                {
                    Id = call.CallId,
                    Type = ParseNeedType(call.Name),
                    Description = call.Arguments?.ToString() ?? call.Name,
                    CreatedAt = DateTime.UtcNow
                };

                if (call.Arguments is IDictionary<string, object?> args)
                {
                    foreach (var (k, v) in args)
                        if (v != null) need.Params[k] = v.ToString()!;
                }

                if (call.Name.Contains("fire_and_forget_"))
                {
                    need.Level = DelegateLevel.FireAndForget;
                    _ = ExecuteL1FunctionAsync(call, l1ChatClient);
                    fulfillTasks[call.CallId] = Task.FromResult(
                        new FunctionResultContent(call.CallId, "dispatched"));
                }
                else if (call.Name.Contains("approve_"))
                {
                    need.Level = DelegateLevel.NeedApproval;
                    fulfillTasks[call.CallId] = ExecuteL1FunctionAsync(call, l1ChatClient);
                }
                else
                {
                    need.Level = DelegateLevel.NeedResult;
                    fulfillTasks[call.CallId] = ExecuteL1FunctionAsync(call, l1ChatClient);
                }

                needs.Add(need);
            }

            await Task.WhenAll(fulfillTasks.Values).ConfigureAwait(false);

            foreach (var (callId, task) in fulfillTasks)
            {
                var result = await task.ConfigureAwait(false);
                history.Add(new ChatMessage(ChatRole.Tool, [result]));
            }

            totalLatency += (DateTime.UtcNow - roundStart).TotalMilliseconds;

            if (round == maxRounds - 1)
                insights.Add($"Max rounds ({maxRounds}) reached");
        }

        return new CollaborationResult
        {
            Text = string.Join("\n\n", fullText),
            Needs = needs,
            Rounds = Math.Min(maxRounds, needs.Count > 0 ? maxRounds : 1),
            TotalTokens = totalTokens,
            TotalLatencyMs = totalLatency,
            Insights = insights
        };
    }

    private static NeedType ParseNeedType(string functionName)
    {
        if (functionName.Contains("file") || functionName.Contains("read") || functionName.Contains("write"))
            return NeedType.File;
        if (functionName.Contains("search") || functionName.Contains("knowledge"))
            return NeedType.Knowledge;
        if (functionName.Contains("sql"))
            return NeedType.Sql;
        if (functionName.Contains("human") || functionName.Contains("ask_user"))
            return NeedType.Human;
        if (functionName.Contains("tool") || functionName.Contains("execute"))
            return NeedType.Tool;
        return NeedType.Question;
    }

    private async Task<FunctionResultContent> ExecuteL1FunctionAsync(
        FunctionCallContent call, IChatClient? l1ChatClient)
    {
        try
        {
            var result = call.Name switch
            {
                "search_knowledge" => await HandleSearchAsync(call, l1ChatClient),
                "read_file" => await HandleFileAsync(call),
                "execute_tool" => await HandleToolAsync(call, l1ChatClient),
                "run_sql" => await HandleSqlAsync(call),
                "ask_human" => await HandleHumanAsync(call),
                "l1_question" => await HandleL1QuestionAsync(call, l1ChatClient),
                "fire_and_forget_task" or "approve_action" => "Task dispatched",
                _ => await HandleL1QuestionAsync(call, l1ChatClient).ConfigureAwait(false)
            };

            return new FunctionResultContent(call.CallId, result?.ToString() ?? "done");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L1 function failed: {Function}", call.Name);
            return new FunctionResultContent(call.CallId, $"Error: {ex.Message}");
        }
    }

    private static async Task<string> HandleSearchAsync(FunctionCallContent call, IChatClient? l1ChatClient)
    {
        var query = ExtractArg(call, "query") ?? "";
        if (l1ChatClient != null)
        {
            var resp = await l1ChatClient.GetResponseAsync(
                $"Search knowledge base: {query}", cancellationToken: CancellationToken.None);
            return resp.Text ?? "";
        }
        return $"Knowledge result for: {query}";
    }

    private static Task<string> HandleFileAsync(FunctionCallContent call)
    {
        var path = ExtractArg(call, "path") ?? "";
        var op = ExtractArg(call, "operation") ?? "read";
        return Task.FromResult($"File: {op} {path}");
    }

    private static Task<string> HandleToolAsync(FunctionCallContent call, IChatClient? l1ChatClient)
    {
        var desc = ExtractArg(call, "description") ?? ExtractArg(call, "tool") ?? "";
        return Task.FromResult($"Tool: {desc}");
    }

    private static Task<string> HandleSqlAsync(FunctionCallContent call)
    {
        var query = ExtractArg(call, "query") ?? ExtractArg(call, "sql") ?? "";
        return Task.FromResult($"SQL: {query[..Math.Min(100, query.Length)]}");
    }

    private async Task<string> HandleHumanAsync(FunctionCallContent call)
    {
        var question = ExtractArg(call, "question") ?? ExtractArg(call, "message") ?? "";
        var timeout = int.TryParse(ExtractArg(call, "timeout") ?? "30", out var t) ? t : 30;
        var answer = await _AskHumanAsync(question, timeout, _humanCallback).ConfigureAwait(false);
        return answer ?? "No human response";
    }

    private static async Task<string> HandleL1QuestionAsync(FunctionCallContent call, IChatClient? l1ChatClient)
    {
        var question = ExtractArg(call, "question") ?? ExtractArg(call, "description") ?? "";
        if (l1ChatClient != null)
        {
            var resp = await l1ChatClient.GetResponseAsync(question, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return resp.Text ?? "";
        }
        return $"L1: {question}";
    }

    private static string? ExtractArg(FunctionCallContent call, string name)
    {
        if (call.Arguments is IDictionary<string, object?> dict &&
            dict.TryGetValue(name, out var val) && val != null)
            return val.ToString();
        return null;
    }

    private async Task<string?> _AskHumanAsync(string question, int timeout,
        Func<string, int, Task<string?>>? callback)
    {
        if (callback == null) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            return await callback(question, timeout).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Human callback timed out after {Timeout}s", timeout);
            return null;
        }
    }

    private static async Task<List<string>> L1PreloadAsync(string userQuery, IChatClient? l1ChatClient)
    {
        var preload = new List<string>();
        if (l1ChatClient == null) return preload;

        try
        {
            var resp = await l1ChatClient.GetResponseAsync(
                $"User query: \"{userQuery}\". What context should be preloaded? Brief bullet list.",
                cancellationToken: CancellationToken.None);
            preload.AddRange((resp.Text ?? "").Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch { }

        return preload;
    }

    private static string BuildDelegationPrompt(List<string> preload, string extraContext)
    {
        var pt = preload.Count > 0
            ? $"\n### Preloaded Context\n{string.Join("\n", preload.Select(p => $"- {p}"))}"
            : "";

        return $"You are an AI assistant with function-calling for delegation.\n"
            + "When you need external help, call a function instead of guessing.\n"
            + "Available: search_knowledge(query), read_file(path), execute_tool(description), "
            + "l1_question(question), ask_human(question, timeout), fire_and_forget_task(description), approve_action(description)\n"
            + "Provide your final answer when done.\n"
            + $"{pt}\n{extraContext}";
    }

    private static List<AITool> BuildL2DelegationFunctions()
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                (string query) => $"searching: {query}",
                "search_knowledge", "Search the knowledge base for information"),
            AIFunctionFactory.Create(
                (string path) => $"reading: {path}",
                "read_file", "Read the contents of a file"),
            AIFunctionFactory.Create(
                (string description) => $"executing: {description}",
                "execute_tool", "Execute a registered tool"),
            AIFunctionFactory.Create(
                (string question) => $"L1: {question}",
                "l1_question", "Ask the L1 fast model a question"),
            AIFunctionFactory.Create(
                (string question, int timeout) => $"Human: {question}",
                "ask_human", "Ask a human for input"),
            AIFunctionFactory.Create(
                (string description) => $"dispatched: {description}",
                "fire_and_forget_task", "Dispatch a task without waiting"),
            AIFunctionFactory.Create(
                (string description) => $"approval: {description}",
                "approve_action", "Request human approval for an action"),
        };
    }

    public async Task<Dictionary<string, object>> ConferenceAsync(
        string problem, Dictionary<string, string> context,
        int maxRounds, IChatClient chatClient)
    {
        var proposals = new List<string>();
        var evaluations = new List<string>();

        for (var r = 0; r < maxRounds; r++)
        {
            var ctx = $"Problem: {problem}\n{string.Join("\n", context.Select(kv => $"{kv.Key}: {kv.Value}"))}";

            var l1 = await chatClient.GetResponseAsync(
                $"L1 (fast): propose quick solution to: {ctx}", cancellationToken: CancellationToken.None);
            proposals.Add(l1.Text ?? "");

            var l2 = await chatClient.GetResponseAsync(
                $"L2 (deep): evaluate proposal and suggest improvements: {l1.Text}",
                cancellationToken: CancellationToken.None);
            evaluations.Add(l2.Text ?? "");
        }

        var final = await chatClient.GetResponseAsync(
            $"Synthesize final JSON with decision/confidence/rationale for: {problem}",
            cancellationToken: CancellationToken.None);

        return new Dictionary<string, object>
        {
            ["decision"] = final.Text ?? "",
            ["proposals"] = proposals,
            ["evaluations"] = evaluations,
            ["rounds"] = maxRounds
        };
    }

    public void SetWorldFact(string key, string value) => _worldState[key] = value;
    public string? GetWorldFact(string key) => _worldState.GetValueOrDefault(key);
    public void SetFeedback(string feedback) => _l2Feedback = feedback;

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cnChars = text.Count(c => c >= 0x4e00 && c <= 0x9fff);
        var enWords = (text.Length - cnChars) / 4;
        return cnChars + Math.Max(1, enWords);
    }
}
