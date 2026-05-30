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
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public BasicContextProvider() : base(null, null, null) { }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var info = BuildContextString();
        return new AIContext
        {
            Instructions = context.AIContext?.Instructions != null
                ? info + "\n" + context.AIContext.Instructions
                : info,
            Messages = context.AIContext?.Messages,
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
        if (_cachedLocation != null) return _cachedLocation;
        try
        {
            var task = _http.GetFromJsonAsync<JsonElement>(
                "http://ip-api.com/json/?fields=city,regionName,country&lang=zh-CN");
            task.Wait(TimeSpan.FromSeconds(3));
            if (!task.IsCompletedSuccessfully) return "未知";
            var json = task.Result;
            var city = json.GetProperty("city").GetString() ?? "";
            var region = json.GetProperty("regionName").GetString() ?? "";
            var country = json.GetProperty("country").GetString() ?? "";
            _cachedLocation = $"{country} {region} {city}";
            return _cachedLocation;
        }
        catch
        {
            return "未知";
        }
    }
}
