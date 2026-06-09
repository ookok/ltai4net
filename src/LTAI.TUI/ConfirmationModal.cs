using LTAI.Core.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public static class ConfirmationModal
{
    private static int SafeWindowWidth
    {
        get { try { return Console.WindowWidth; } catch { return 80; } }
    }

    public static ConfirmChoice Show(string title, string message, string details = "", string? extraInfo = null)
    {
        return ShowSync(title, message, details, extraInfo);
    }

    public static async Task<ConfirmChoice> ShowInlineAsync(
        Layout layout, Action refresh,
        string title, string message,
        string details = "", string? extraInfo = null)
    {
        ChatLayout.ConfirmSelection = 0;
        RenderModal(layout, title, message, details, extraInfo);
        refresh();
        var tcs = new TaskCompletionSource<ConfirmChoice>();
        ChatLayout.PendingConfirmTcs = tcs;
        ChatLayout.PendingConfirmDetails = details;
        ChatLayout.PendingConfirmTitle = title;
        ChatLayout.PendingConfirmMessage = message;
        ChatLayout.PendingConfirmExtra = extraInfo;
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        }
        catch (TimeoutException) { return ConfirmChoice.No; }
        finally { ChatLayout.PendingConfirmTcs = null; }
    }

    internal static bool HandleConfirmKey(ConsoleKeyInfo key, out ConfirmChoice choice, out bool selectionChanged)
    {
        selectionChanged = false;
        // Direct letter keys
        choice = key.KeyChar switch
        {
            'y' or 'Y' => ConfirmChoice.Yes,
            'a' or 'A' => ConfirmChoice.Always,
            'n' or 'N' => ConfirmChoice.No,
            'd' or 'D' => ConfirmChoice.Details,
            _ when key.Key == ConsoleKey.Escape => ConfirmChoice.No,
            _ when key.Key == ConsoleKey.Enter => CurrentSelectionToChoice(),
            _ => (ConfirmChoice)(-1),
        };
        if ((int)choice >= 0) return true;

        // Arrow key navigation
        if (key.Key == ConsoleKey.LeftArrow)
        {
            ChatLayout.ConfirmSelection = Math.Max(0, ChatLayout.ConfirmSelection - 1);
            selectionChanged = true;
        }
        else if (key.Key == ConsoleKey.RightArrow)
        {
            ChatLayout.ConfirmSelection = Math.Min(3, ChatLayout.ConfirmSelection + 1);
            selectionChanged = true;
        }
        return false;
    }

    private static ConfirmChoice CurrentSelectionToChoice()
    {
        return ChatLayout.ConfirmSelection switch
        {
            0 => ConfirmChoice.Yes,
            1 => ConfirmChoice.Always,
            2 => ConfirmChoice.No,
            3 => ConfirmChoice.Details,
            _ => ConfirmChoice.No,
        };
    }

    internal static void ReRender(Layout layout)
    {
        if (layout != null)
        {
            RenderModal(layout, ChatLayout.PendingConfirmTitle ?? "",
                ChatLayout.PendingConfirmMessage ?? "",
                ChatLayout.PendingConfirmDetails ?? "",
                ChatLayout.PendingConfirmExtra);
        }
    }

    internal static void RenderModal(
        Layout layout,
        string title, string message,
        string details, string? extraInfo)
    {
        var panel = BuildModalPanel(title, message, details, extraInfo);
        layout["Messages"].Update(panel);
    }

    private static Panel BuildModalPanel(
        string title, string message,
        string details, string? extraInfo)
    {
        var rows = new List<IRenderable>();
        var termWidth = Math.Min(SafeWindowWidth, 120);

        var msgText = TruncateForWidth(message, termWidth - 8);
        rows.Add(new Markup($"[bold]{msgText.EscapeMarkup()}[/]"));

        if (!string.IsNullOrEmpty(extraInfo))
        {
            rows.Add(new Text(""));
            var infoText = TruncateForWidth(extraInfo, termWidth - 8);
            rows.Add(new Markup($"[grey]{infoText.EscapeMarkup()}[/]"));
        }

        rows.Add(new Text(""));
        rows.Add(new Rule("[grey]请选择[/]") { Style = Style.Parse("grey") });
        rows.Add(new Text(""));

        // Build option rows with selection highlighting
        var sel = ChatLayout.ConfirmSelection;
        var options = new[]
        {
            (key: 'Y', text: "允许一次", color: "yellow"),
            (key: 'A', text: "总是允许", color: "green"),
            (key: 'N', text: "拒绝", color: "red"),
            (key: 'D', text: "查看详情", color: "blue"),
        };

        var keysTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").PadRight(0))
            .AddColumn(new TableColumn("").PadLeft(0))
            .AddColumn(new TableColumn("").PadRight(0))
            .AddColumn(new TableColumn("").PadLeft(0))
            .AddColumn(new TableColumn("").PadRight(0))
            .AddColumn(new TableColumn("").PadLeft(0))
            .AddColumn(new TableColumn("").PadRight(0));

        var cells = new List<IRenderable>();
        for (int i = 0; i < options.Length; i++)
        {
            var (k, t, c) = options[i];
            if (i == sel)
                cells.Add(new Markup($"[bold black on {c}] {k} {t} [/]"));
            else
                cells.Add(new Markup($"[bold {c}]{k}[/] {t}"));
            if (i < options.Length - 1)
                cells.Add(new Text("  "));
        }
        keysTable.AddRow(cells.ToArray());
        rows.Add(keysTable);

        // Hint text
        rows.Add(new Text(""));
        rows.Add(new Markup($"[grey]← → 选择  Enter 确认  Y/A/N/D 快捷键[/]"));

        return new Panel(new Rows(rows.ToArray()))
        {
            Header = new PanelHeader($"[bold yellow]⚠️  {title.EscapeMarkup()}[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(3, 2, 3, 2),
        };
    }

    private static ConfirmChoice ShowSync(string title, string message, string details, string? extraInfo)
    {
        AnsiConsole.Write(BuildModalPanel(title, message, details, extraInfo));

        var confirmPrompt = new SelectionPrompt<string>()
            .Title("[grey]选择操作:[/]")
            .PageSize(6)
            .HighlightStyle(new Style(Color.Black, Color.Cyan))
            .AddChoices("[Y]  允许一次", "[A]  总是允许", "[N]  拒绝", "[D]  查看详情");
        var confirmChoice = AnsiConsole.Prompt(confirmPrompt);

        if (confirmChoice.StartsWith("[Y]")) return ConfirmChoice.Yes;
        if (confirmChoice.StartsWith("[A]")) return ConfirmChoice.Always;
        if (confirmChoice.StartsWith("[D]"))
        {
            ShowDetails(details, title);
            return ShowSync(title, message, details, extraInfo);
        }
        return ConfirmChoice.No;
    }

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
            var pageLines = lines.Skip(currentPage * pageSize).Take(pageSize).ToList();
            var pageText = string.Join("\n", pageLines);
            var pagePanel = new Panel(
                new Rows(new Markup(pageText.EscapeMarkup()), new Text(""),
                    new Markup($"[grey]第 {currentPage + 1}/{totalPages} 页[/]")))
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
                .PageSize(5).AddChoices(navChoices));
            if (nav == "返回") return;
            if (nav == "上一页") currentPage--;
            if (nav == "下一页") currentPage++;
        }
    }

    private static string TruncateForWidth(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= maxWidth ? text : text[..maxWidth] + "...";
    }
}
