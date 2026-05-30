using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// 在每个请求前注入基本上下文：当前时间、IP归属地、系统信息。
/// 比依赖 LLM 调 tool 更可靠，且只占 ~50 tokens。
/// </summary>
public sealed class BasicContextProvider : AIContextProvider
{
    private static string? _cachedLocation;

    public BasicContextProvider() : base(null, null, null) { }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var info = BuildContextString();
        var confirmRule = "\n[操作规则] 当工具返回「尚未获得权限」时，请向用户展示路径并询问是否允许，用户同意后重新调用相同工具并设置 confirm=true。不要尝试其他工具代替。";
        var msg = new ChatMessage(ChatRole.System, $"[基础信息] {info}{confirmRule}");

        var msgs = context.AIContext?.Messages?.ToList() ?? new List<ChatMessage>();
        msgs.Insert(0, msg); // 插在最前面，让 LLM 最先看到

        return new AIContext
        {
            Instructions = context.AIContext?.Instructions,
            Messages = msgs,
            Tools = context.AIContext?.Tools,
        };
    }

    private static string BuildContextString()
    {
        var now = DateTime.Now;
        var weekday = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一", DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三", DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五", DayOfWeek.Saturday => "星期六",
            DayOfWeek.Sunday => "星期日", _ => now.DayOfWeek.ToString()
        };

        var dateStr = $"当前时间: {now:yyyy年MM月dd日} {weekday} {now:HH:mm:ss}";

        // 异步获取位置（首次阻塞，后续使用缓存）
        var loc = GetLocation();

        return $"[基本信息] {dateStr} | 位置: {loc} | 系统: Windows {Environment.OSVersion.VersionString}";
    }

    private static string GetLocation()
    {
        // 跳过 ip-api.com 查询（节省 0-3s 首 token 延迟），默认返回本地
        return _cachedLocation ??= "本地";
    }
}
