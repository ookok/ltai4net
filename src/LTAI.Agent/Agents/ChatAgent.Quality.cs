using System.Text.Json;
using LTAI.Agent.Pipeline;
using LTAI.Core.Safety;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed partial class ChatAgent
{
    private static string ApplyBlockedOutput(string text)
    {
        var reason = SafetyCoordinator.ConsumeBlock();
        if (reason != null)
            return $"[Content blocked by safety filter. Reason: {reason}]";
        return text;
    }

    /// <summary>LLM-as-Judge quality evaluation.</summary>
    private async Task<(bool IsAdequate, string? Reason, int Score)> JudgeResponseQualityAsync(
        string message, string response, CancellationToken ct)
    {
        if (_steerJudge == null)
            return (true, "no steer model configured — assuming adequate", 5);
        try
        {
            var judgeMessages = new ChatMessage[]
            {
                new(ChatRole.System,
                    "You are a response quality judge. Given a user query and an AI response, " +
                    "determine if the response is adequate. " +
                    "Criteria: relevant, helpful, not vague/hedging, not refusing, not hallucinating.\n" +
                    "Respond with ONLY valid JSON like: {\"adequate\": true, \"reason\": \"...\", \"self_score\": 4}\n" +
                    "Score: 1-5 (5=excellent). Adequate if score >= 3 and reason indicates adequate."),
                new(ChatRole.User, $"Query: {message}\n\nResponse: {response}")
            };
            var judgeResult = await _steerJudge.GetResponseAsync(judgeMessages, cancellationToken: ct)
                .ConfigureAwait(false);
            var raw = judgeResult.Text ?? "";
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
                var root = doc.RootElement;
                var adequate = root.TryGetProperty("adequate", out var a) && a.GetBoolean();
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var score = root.TryGetProperty("self_score", out var s) ? s.GetInt32() : 0;
                var isConfident = score >= _judgeConfidenceThreshold;
                return (adequate && isConfident, reason ?? (adequate ? null : "judge deemed inadequate"), score);
            }
            return (true, null, 5);
        }
        catch
        {
            return (true, null, 0);
        }
    }

    private static L1State BuildL1State(string message, string response, AgentResponse result)
    {
        var gap = EstimateCoverageGap(message, response);
        return new L1State
        {
            Label = gap > 0.4 ? "escalate" : "handled",
            SupportCount = CountSupportingEvidence(response),
            Gap = gap,
            ToolCalls = result.Messages?
                .SelectMany(m => m.Contents?.OfType<FunctionCallContent>() ?? [])
                .Select(fc => fc.Name ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [],
            EscalationReason = gap > 0.5 ? $"coverage gap={gap:F2}" : null
        };
    }

    private static double EstimateCoverageGap(string message, string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return 1.0;
        var msgLower = message.ToLowerInvariant();
        var respLower = response.ToLowerInvariant();
        var keywords = msgLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3).ToHashSet();
        return keywords.Count == 0 ? 0 : (double)keywords.Count(k => !respLower.Contains(k)) / keywords.Count;
    }

    private static int CountSupportingEvidence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('-') || t.StartsWith('*') || t.StartsWith("1.") || t.StartsWith("2."))
                count++;
        }
        return count;
    }

    private static double EstimateResponseEntropy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1.0;
        var hedgeWords = new[] {
            "不确定", "可能", "也许", "大概", "估计", "似乎", "推测",
            "疑似", "貌似", "好像", "或许是", "按理说", "看样子", "猜测",
            "通常情况下", "一般来说", "理论上", "某种程度上",
            "maybe", "perhaps", "probably", "possibly", "might", "could be",
            "sometimes", "usually", "generally", "typically", "often",
            "likely", "unlikely", "presumably", "arguably", "apparently",
            "seems", "appears", "suggests", "indicates",
            "かもしれない", "でしょう", "たぶん", "おそらく",
            "아마도", "아마",
        };
        var count = hedgeWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        return Math.Min(1.0, count * 0.15);
    }

    private static double EstimateValueOfInformation(string query, string response, double entropy)
    {
        if (entropy > 0.5) return 0.8;
        if (string.IsNullOrWhiteSpace(response)) return 1.0;
        var qLower = query.ToLowerInvariant();
        var ambiguous = new[] { "what", "how", "why", "when", "where", "which",
            "什么", "如何", "怎么", "为什么", "何时", "哪里" };
        if (ambiguous.Any(w => qLower.Contains(w))) return 0.5 + entropy * 0.3;
        return entropy * 0.5;
    }

    private async Task<string> EnforceAndReflectAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        _correctionDepth.Value++;
        if (_correctionDepth.Value > _correctionLoopMaxDepth) { _correctionDepth.Value = 0; return text; }

        if (_proAgent == null) return text;

        var hasRefusal = _escalationDecider.ContainsRefusalPatterns(text);
        var hasPlaceholder = text.Contains("{{") || text.Contains("TODO");
        var hasHedgeWords = ContainsHedgeWords(text);

        if (!hasRefusal && text.Length >= 15 && !hasPlaceholder && !hasHedgeWords)
            return text;

        var safeOriginal = JsonSerializer.Serialize(originalMessage);
        var stage1Prompt = $"""
            - 不要拒绝、猜测或编造
            - 确保回答完整（不含占位符）

            用户原始问题（JSON字符串）：{safeOriginal}

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

            var stage2Prompt = $"你必须使用工具来回答用户问题。不要拒绝、不要猜测。\n\n用户问题是（JSON字符串）: {safeOriginal}";
            var result2 = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, stage2Prompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var refined2 = result2.Messages?.LastOrDefault()?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(refined2) && refined2.Length > 10)
                return $"[工具]\n\n{refined2}";
        }
        catch
        {
            _logger?.LogWarning("Swallowing exception in ChatAgent.cs");
        }
        return text;
    }

    private static bool ContainsHedgeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 30) return false;
        var hedgeCount = 0;
        var lower = text.ToLowerInvariant();
        var hedgeWords = new[] {
            "不确定", "可能", "也许", "大概", "估计", "似乎", "推测",
            "maybe", "perhaps", "probably", "possibly", "might", "could be",
            "seems", "appears", "suggests", "indicates",
            "かもしれません", "でしょう", "たぶん",
            "아마도", "아마",
        };
        foreach (var word in hedgeWords)
        {
            var idx = 0;
            while ((idx = lower.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
            {
                hedgeCount++;
                idx += word.Length;
            }
        }
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return hedgeCount >= 3 || (wordCount > 0 && (double)hedgeCount / wordCount > 0.05);
    }

    private static void RefreshModeObserver(AgentSession session)
    {
        var jso = Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions;
        try
        {
            var modeState = session.StateBag.GetValue<Tooling.ObservableAgentModeState>("AgentModeProvider", jso);
            if (modeState != null)
                Tooling.AgentModeObserver.CurrentMode = modeState.CurrentMode ?? "chat";
        }
        catch { }

        try
        {
            var todoState = session.StateBag.GetValue<Tooling.ObservableTodoState>("TodoProvider", jso);
            if (todoState?.Items is { Count: > 0 })
            {
                Tooling.AgentModeObserver.TotalTodos = todoState.Items.Count;
                Tooling.AgentModeObserver.RemainingTodos = todoState.Items.Count(t => !t.IsComplete);
                var sb = new System.Text.StringBuilder();
                foreach (var t in todoState.Items)
                {
                    var icon = t.IsComplete ? "✅" : "⬜";
                    sb.AppendLine($"{icon} {t.Title}" + (t.Description != null ? $": {t.Description}" : ""));
                }
                Tooling.AgentModeObserver.TodoSummary = sb.ToString();
            }
            else
            {
                Tooling.AgentModeObserver.TotalTodos = 0;
                Tooling.AgentModeObserver.RemainingTodos = 0;
                Tooling.AgentModeObserver.TodoSummary = null;
            }
        }
        catch { }
    }

    private static GrammarCheckResult ParseGrammarCheckResult(List<ChatMessage> errorMessages)
    {
        var errorCount = errorMessages.Count;
        var firstMsg = errorMessages.FirstOrDefault()?.Text ?? "";
        var parts = firstMsg.Split(':', 3);
        var filePath = parts.Length > 0 ? parts[0].Trim() : "";
        var errorType = parts.Length > 2 ? parts[2].Trim() : "syntax";
        if (errorType.Length > 40) errorType = errorType[..40];
        return new GrammarCheckResult(errorType, filePath, errorCount, 0, 0);
    }
}
