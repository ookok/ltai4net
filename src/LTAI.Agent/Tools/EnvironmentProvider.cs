using System.Runtime.InteropServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

public sealed class EnvironmentProvider : AIContextProvider
{
    public EnvironmentProvider() : base(null, null, null) { }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var weekday = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一", DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三", DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五", DayOfWeek.Saturday => "星期六",
            DayOfWeek.Sunday => "星期日", _ => now.DayOfWeek.ToString()
        };

        var info = $"当前时间: {now:yyyy年MM月dd日} {weekday} {now:HH:mm:ss} | "
            + $"系统: {RuntimeInformation.OSDescription} | "
            + $"平台: {RuntimeInformation.ProcessArchitecture} | "
            + $"目录: {Environment.CurrentDirectory}";

        var msg = new ChatMessage(ChatRole.System, $"[环境信息] {info}");

        var msgs = context.AIContext?.Messages?.ToList() ?? [];
        msgs.Insert(0, msg);

        return ValueTask.FromResult(new AIContext
        {
            Instructions = context.AIContext?.Instructions,
            Messages = msgs,
            Tools = context.AIContext?.Tools,
        });
    }
}
