using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// Injects the current date/time when the user asks a time-related question.
/// Uses FastEmb intent classification (zero API cost) to detect time queries.
/// Preserves prefix cache for non-time queries.
/// </summary>
public sealed class DateContextProvider : AIContextProvider
{
    private string? _cachedDate;

    public DateContextProvider() : base(null, null, null) { }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return ValueTask.FromResult(context.AIContext!);

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null) return ValueTask.FromResult(context.AIContext!);

        if (!IsTimeQuery(userMsg.Text))
            return ValueTask.FromResult(context.AIContext!);

        System.Diagnostics.Debug.WriteLine($"[DateContextProvider] injecting date for: \"{userMsg.Text}\"");
        var dateStr = _cachedDate ??= FormatDate();
        var dateMessage = new ChatMessage(ChatRole.System,
            $"[当前真实日期时间: {dateStr}] 请直接用这个日期回答。");

        var messages = context.AIContext?.Messages?.ToList() ?? new List<ChatMessage>();
        // Insert after the first system message (before user message)
        var insertAt = Math.Min(1, messages.Count);
        messages.Insert(insertAt, dateMessage);

        return ValueTask.FromResult(new AIContext
        {
            Instructions = context.AIContext?.Instructions,
            Messages = messages,
            Tools = context.AIContext?.Tools,
        });
    }

    private static bool IsTimeQuery(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        // Quick keyword check first (zero cost)
        if (lower.Contains("星期") || lower.Contains("周") || lower.Contains("几号") ||
            lower.Contains("几月") || lower.Contains("日期") || lower.Contains("时间") ||
            lower.Contains("现在") || lower.Contains("几点") || lower.Contains("今天") ||
            lower.Contains("明天") || lower.Contains("昨天") || lower.Contains("号") ||
            lower.Contains("年份") || lower.Contains("哪年") || lower.Contains("什么季节"))
            return true;
        return false;
    }

    private static string FormatDate()
    {
        var now = DateTime.Now;
        var weekday = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            DayOfWeek.Sunday => "星期日",
            _ => now.DayOfWeek.ToString()
        };
        return $"{now:yyyy年MM月dd日} {weekday} {now:HH:mm:ss}";
    }
}
