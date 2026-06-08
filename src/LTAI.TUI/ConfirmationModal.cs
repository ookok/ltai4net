// 工具安全确认模态窗口
// 使用 Spectre.Console 渲染带边框的交互面板，
// 支持键盘快捷键选择，替代 LLM 往返确认流程。

using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

/// <summary>用户确认结果。</summary>
public enum ConfirmChoice
{
    /// <summary>允许一次。</summary>
    Yes,
    /// <summary>总是允许（会话级）。</summary>
    Always,
    /// <summary>拒绝。</summary>
    No,
    /// <summary>查看详情（分页显示完整内容）。</summary>
    Details,
}

/// <summary>
/// Spectre.Console 模态确认窗口。
/// 在终端交互区域渲染带边框面板，纯键控操作。
///
/// 使用方式：
/// <code>
/// var choice = ConfirmationModal.Show("执行命令", cmdText, details);
/// if (choice == ConfirmChoice.Yes) { ... }
/// </code>
/// </summary>
public static class ConfirmationModal
{
    private static int SafeWindowWidth
    {
        get { try { return Console.WindowWidth; } catch { return 80; } }
    }

    /// <summary>
    /// 显示确认模态窗口并等待用户键盘选择。
    /// 选项:
    ///   [Y] 允许一次    [A] 总是允许    [N] 拒绝    [D] 查看详情
    /// </summary>
    /// <param name="title">标题（如"执行命令"）。</param>
    /// <param name="message">主消息（如命令文本）。</param>
    /// <param name="details">详情（完整输出/路径等，可按 D 查看）。</param>
    /// <param name="extraInfo">可选的额外信息行（如目录、参数）。</param>
    /// <returns>用户的选择。</returns>
    public static ConfirmChoice Show(
        string title,
        string message,
        string details = "",
        string? extraInfo = null)
    {
        return ShowInline(null!, null!, title, message, details, extraInfo, useAnsiConsole: true);
    }

    /// <summary>
    /// 在 Live Display 的 Layout 面板内显示确认模态窗口。
    /// </summary>
    public static ConfirmChoice ShowInline(
        Layout layout, LiveDisplayContext ctx,
        string title, string message,
        string details = "", string? extraInfo = null)
    {
        return ShowInline(layout, ctx, title, message, details, extraInfo, useAnsiConsole: false);
    }

    private static ConfirmChoice ShowInline(
        Layout? layout, LiveDisplayContext? ctx,
        string title, string message,
        string details, string? extraInfo,
        bool useAnsiConsole)
    {
        // 构建模态面板内容
        var rows = new List<IRenderable>();
        var termWidth = Math.Min(SafeWindowWidth, 120);

        // 消息区
        var msgText = TruncateForWidth(message, termWidth - 8);
        rows.Add(new Markup($"[bold]{msgText.EscapeMarkup()}[/]"));

        // 额外信息
        if (!string.IsNullOrEmpty(extraInfo))
        {
            rows.Add(new Text(""));
            var infoText = TruncateForWidth(extraInfo, termWidth - 8);
            rows.Add(new Markup($"[grey]{infoText.EscapeMarkup()}[/]"));
        }

        // 分隔线
        rows.Add(new Text(""));
        rows.Add(new Rule("[grey]请选择[/]") { Style = Style.Parse("grey") });
        rows.Add(new Text(""));

        // 快捷键说明 — 使用 Table 布局对齐
        var keysTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").PadRight(0))
            .AddColumn(new TableColumn("").PadLeft(0))
            .AddColumn(new TableColumn("").PadRight(0))
            .AddColumn(new TableColumn("").PadLeft(0))
            .AddColumn(new TableColumn("").PadRight(0))
            .AddColumn(new TableColumn("").PadLeft(0))
            .Width(termWidth - 6);

        keysTable.AddRow(
            new Markup("[bold yellow]Y[/] 允许一次"),
            new Text("  "),
            new Markup("[bold green]A[/] 总是允许"),
            new Text("  "),
            new Markup("[bold red]N[/] 拒绝"),
            new Text("  "),
            new Markup("[bold blue]D[/] 查看详情")
        );
        rows.Add(keysTable);

        // 构建面板
        var panel = new Panel(new Rows(rows.ToArray()))
        {
            Header = new PanelHeader($"[bold yellow]⚠️  {title.EscapeMarkup()}[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(3, 2, 3, 2),
            Expand = true,
        };

        // 在 live 区域下方渲染模态窗口
        if (useAnsiConsole)
        {
            AnsiConsole.Write(panel);
        }
        else if (layout != null && ctx != null)
        {
            layout["Messages"].Update(panel);
            ctx.Refresh();
        }

        // 使用 SelectionPrompt 替代裸 Console.ReadKey
        var confirmPrompt = new SelectionPrompt<string>()
            .Title("[grey]选择操作:[/]")
            .PageSize(6)
            .HighlightStyle(new Style(Color.Black, Color.Cyan))
            .AddChoices(
                "[Y]  允许一次",
                "[A]  总是允许",
                "[N]  拒绝",
                "[D]  查看详情");
        var confirmChoice = AnsiConsole.Prompt(confirmPrompt);

        if (confirmChoice.StartsWith("[Y]"))
            return ConfirmChoice.Yes;
        if (confirmChoice.StartsWith("[A]"))
            return ConfirmChoice.Always;
        if (confirmChoice.StartsWith("[D]"))
        {
            ShowDetails(details, title);
            // 详情关闭后重新渲染模态窗口
            if (useAnsiConsole) { AnsiConsole.Write(panel); }
            else if (layout != null && ctx != null) { layout["Messages"].Update(panel); ctx.Refresh(); }
            // After details, ask again
            return ShowInline(layout, ctx, title, message, details, extraInfo, useAnsiConsole);
        }
        return ConfirmChoice.No;
    }

    /// <summary>
    /// 在确认窗口中显示详细内容（支持分页）。
    /// </summary>
    private static void ShowDetails(string details, string context)
    {
        var lines = details.Split('\n');
        const int pageSize = 20;

        if (lines.Length <= pageSize + 2)
        {
            var detailPanel = new Panel(
                new Markup(details.Length > 2000
                    ? details[..2000].EscapeMarkup() + "\n[grey]...(内容过长，仅显示前 2000 字符)[/]"
                    : details.EscapeMarkup()))
            {
                Header = new PanelHeader($"[bold blue]📄 {context.EscapeMarkup()} — 详情[/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(2, 1, 2, 1),
                Expand = true,
            };
            AnsiConsole.Write(detailPanel);
            AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[dim]按 Enter 返回[/]").PageSize(3).AddChoices("返回"));
            return;
        }

        int totalPages = (lines.Length + pageSize - 1) / pageSize;
        int currentPage = 0;

        while (currentPage < totalPages)
        {
            var pageLines = lines
                .Skip(currentPage * pageSize)
                .Take(pageSize)
                .ToList();
            var pageText = string.Join("\n", pageLines);

            var pagePanel = new Panel(
                new Rows(
                    new Markup(pageText.EscapeMarkup()),
                    new Text(""),
                    new Markup($"[grey]第 {currentPage + 1}/{totalPages} 页[/]")
                ))
            {
                Header = new PanelHeader($"[bold blue]📄 {context.EscapeMarkup()} — 详情[/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(2, 1, 2, 1),
                Expand = true,
            };
            AnsiConsole.Write(pagePanel);

            var navChoices = new List<string> { "返回" };
            if (currentPage > 0) navChoices.Add("上一页");
            if (currentPage < totalPages - 1) navChoices.Add("下一页");
            var nav = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"[dim]第 {currentPage + 1}/{totalPages} 页[/]")
                .PageSize(5)
                .AddChoices(navChoices));

            if (nav == "返回") return;
            if (nav == "上一页") currentPage--;
            if (nav == "下一页") currentPage++;
        }
    }

    private static string TruncateForWidth(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 简单截断：按字符数
        return text.Length <= maxWidth ? text : text[..maxWidth] + "...";
    }
}
