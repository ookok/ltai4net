using System.Text;
using LTAI.AI;
using LTAI.Core.Commands;
using Spectre.Console;

using AITool = LTAI.AI.ToolRegistry.ToolDef;
using System.Collections.Generic;

namespace LTAI.TUI.Services;

public sealed class ToolsCommandService : ICommandService
{
    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        ToolsCommand tc => Task.FromResult(HandleToolsCommand(tc.Args)),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private CommandResult HandleToolsCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        if (sub == "domain" && parts.Length > 1)
            return ListToolsByDomain(parts[1]);

        return ListAllTools();
    }

    private static CommandResult ListAllTools()
    {
        var all = ToolRegistry.AllTools;
        if (all.Count == 0)
            return new SuccessResult("[yellow]没有已注册的工具（尚未初始化）[/]");

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]已注册的工具 ({all.Count})[/]\n");

        var groups = all.GroupBy(t => string.IsNullOrEmpty(t.Domain) ? "default" : t.Domain)
            .OrderBy(g => g.Key);

        foreach (var g in groups)
        {
            sb.AppendLine($"[bold]{g.Key.EscapeMarkup()}[/]");
            foreach (var t in g.OrderBy(x => x.Name))
            {
                var desc = t.Description.EscapeMarkup();
                sb.AppendLine($"  · [cyan]{t.Name.EscapeMarkup()}[/] — {desc}");
            }
            sb.AppendLine();
        }
        return new SuccessResult(sb.ToString());
    }

    private static CommandResult ListToolsByDomain(string domain)
    {
        var tools = ToolRegistry.GetToolsByDomain(domain);
        if (tools.Count == 0)
            return new SuccessResult($"[yellow]域 '{domain.EscapeMarkup()}' 中没有已注册的工具[/]");

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]域 '{domain.EscapeMarkup()}' 的工具 ({tools.Count})[/]\n");

        var domainIcon = domain.ToLowerInvariant() switch
        {
            "chart" or "multimedia" => "📊",
            "data" or "database" => "🗄️",
            "document" or "office" => "📄",
            "debug" or "diagnostics" => "🔍",
            "security" or "crypto" => "🔒",
            "web" or "network" => "🌐",
            "search" or "retrieval" => "🔎",
            "memory" or "knowledge" => "🧠",
            "git" or "version control" => "🔀",
            "file" or "filesystem" => "📁",
            "pkg" or "package" => "📦",
            "system" or "shell" => "⚙️",
            "code" or "analysis" => "💻",
            "task" or "queue" => "📋",
            _ => "🔧",
        };

        // Domain-specific visual summary
        sb.AppendLine($"  [dim]{domainIcon} {domain.EscapeMarkup()} 域 — {tools.Count} 个工具[/]\n");

        // Build domain-specific visualization
        switch (domain.ToLowerInvariant())
        {
            case "chart":
                RenderChartDomain(sb, tools);
                break;
            case "data":
            case "database":
                RenderDataDomain(sb, tools);
                break;
            case "document":
            case "office":
                RenderDocumentDomain(sb, tools);
                break;
            case "search":
                RenderSearchDomain(sb, tools);
                break;
            case "memory":
            case "knowledge":
                RenderMemoryDomain(sb, tools);
                break;
            default:
                RenderDefaultDomain(sb, tools);
                break;
        }

        return new SuccessResult(sb.ToString());
    }

    private static void RenderChartDomain(StringBuilder sb, IReadOnlyList<AITool> tools)
    {
        sb.AppendLine("  [bold]📈 图表生成工具[/]");
        var sparkline = string.Join("", Enumerable.Range(0, 20).Select(_ =>
            "▁▂▃▄▅▆▇█"[Random.Shared.Next(8)]));
        sb.AppendLine($"  [green]{sparkline}[/]  [dim](示例火花线)[/]");
        sb.AppendLine();
        foreach (var t in tools.OrderBy(x => x.Name))
        {
            var lower = t.Name.ToLowerInvariant();
            var icon = lower.Contains("bar") ? "📊"
                : lower.Contains("line") ? "📈"
                : lower.Contains("pie") ? "🥧"
                : lower.Contains("scatter") ? "🔵"
                : "📉";
            sb.AppendLine($"  {icon} [cyan]{t.Name.EscapeMarkup()}[/] — {t.Description.EscapeMarkup()}");
        }
        sb.AppendLine("\n  [dim]提示: AI 可动态生成图表，结果以 ASCII 火花线显示[/]");
    }

    private static void RenderDataDomain(StringBuilder sb, IReadOnlyList<AITool> tools)
    {
        sb.AppendLine("  [bold]🗄️ 数据工具[/]");
        foreach (var t in tools.Take(5))
        {
            var lower = t.Name.ToLowerInvariant();
            var usage = lower.Contains("query") ? "SQL 查询"
                : lower.Contains("csv") ? "CSV 处理"
                : lower.Contains("transform") ? "数据转换"
                : lower.Contains("import") ? "数据导入"
                : t.Description[..Math.Min(t.Description.Length, 20)];
            var example = lower.Contains("sql") ? "SELECT * FROM ..."
                : lower.Contains("csv") ? "Parse CSV → Table"
                : "—";
            sb.AppendLine($"  · [cyan]{t.Name.EscapeMarkup(),-20}[/][dim]{usage,-12}[/][grey]{example.EscapeMarkup()}[/]");
        }
        sb.AppendLine("\n  [dim]提示: 数据库查询结果自动渲染为 Spectre.Console 表格[/]");
    }

    private static void RenderDocumentDomain(StringBuilder sb, IReadOnlyList<AITool> tools)
    {
        sb.AppendLine("  [bold]📄 文档工具[/]");
        sb.AppendLine();
        foreach (var t in tools.OrderBy(x => x.Name))
        {
            var lower = t.Name.ToLowerInvariant();
            var fmt = lower.Contains("pdf") ? "📕"
                : lower.Contains("docx") || lower.Contains("word") ? "📘"
                : lower.Contains("html") ? "🌐"
                : lower.Contains("markdown") || lower.Contains("md") ? "📝"
                : "📄";
            sb.AppendLine($"  {fmt} [cyan]{t.Name.EscapeMarkup()}[/] — {t.Description.EscapeMarkup()}");
        }
        sb.AppendLine("\n  [dim]支持格式: .pdf · .docx · .md · .html · .txt[/]");
    }

    private static void RenderSearchDomain(StringBuilder sb, IReadOnlyList<AITool> tools)
    {
        sb.AppendLine("  [bold]🔎 搜索工具[/]");
        sb.AppendLine();
        foreach (var t in tools.OrderBy(x => x.Name))
        {
            var lower = t.Name.ToLowerInvariant();
            var engine = lower.Contains("web") ? "🌐 网页"
                : lower.Contains("vector") ? "🧠 向量"
                : lower.Contains("code") || lower.Contains("graph") ? "💻 代码"
                : lower.Contains("knowledge") ? "📚 知识"
                : "📋 通用";
            sb.AppendLine($"  [cyan]{t.Name.EscapeMarkup(),-22}[/]{engine} — {t.Description.EscapeMarkup()}");
        }
        sb.AppendLine("\n  [dim]搜索范围: 网页 · 向量库 · 代码图 · 知识图谱[/]");
    }

    private static void RenderMemoryDomain(StringBuilder sb, IReadOnlyList<AITool> tools)
    {
        sb.AppendLine("  [bold]🧠 记忆工具[/]");
        sb.AppendLine();
        sb.AppendLine("  [grey]┌──────────────────────────┐[/]");
        sb.AppendLine("  [grey]│[/] 🧠  PalaceStore            [grey]│[/]");
        sb.AppendLine("  [grey]│[/] 📦  长期记忆存储           [grey]│[/]");
        sb.AppendLine("  [grey]│[/] 🔄  自动合并               [grey]│[/]");
        sb.AppendLine("  [grey]└──────────────────────────┘[/]");
        sb.AppendLine();
        foreach (var t in tools.OrderBy(x => x.Name))
        {
            sb.AppendLine($"  [cyan]{t.Name.EscapeMarkup()}[/] — {t.Description.EscapeMarkup()}");
        }
        sb.AppendLine("\n  [dim]记忆自动提取和合并，可通过 MemoryBrowser 查看[/]");
    }

    private static void RenderDefaultDomain(StringBuilder sb, IReadOnlyList<AITool> tools)
    {
        foreach (var t in tools.OrderBy(x => x.Name))
        {
            var desc = t.Description.EscapeMarkup();
            sb.AppendLine($"  · [cyan]{t.Name.EscapeMarkup()}[/] — {desc}");
        }
    }
}
