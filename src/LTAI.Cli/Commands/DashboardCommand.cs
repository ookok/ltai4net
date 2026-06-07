using Spectre.Console;
using LTAI.Core.Configuration;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleDashboard()
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]实时仪表盘[/]");
        table.AddColumn("指标"); table.AddColumn("值");
        table.AddRow("当前模型", UsageTracker.ActiveModel);
        table.AddRow("输入 Token", UsageTracker.PromptTokens.ToString("N0"));
        table.AddRow("输出 Token", UsageTracker.CompletionTokens.ToString("N0"));
        table.AddRow("请求次数", UsageTracker.Requests.ToString("N0"));
        table.AddRow("缓存命中", $"{UsageTracker.CacheHitRate:F1}%");
        table.AddRow("预估费用", UsageTracker.CostDisplay);
        table.AddRow("余额", UsageTracker.BalanceDisplay);
        AnsiConsole.Write(table);

        var pct = UsageTracker.ContextRatio();
        AnsiConsole.Write(new BarChart().Width(50).HideValues()
            .AddItem("上下文", pct * 100, Color.Yellow)
            .AddItem("剩余", (1 - pct) * 100, Color.Grey35));
        return 0;
    }
}
