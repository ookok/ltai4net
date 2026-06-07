// 命令选择器渲染 — 纯静态 UI 构建，无交互逻辑
// 交互由 ChatLayout 的输入任务线程处理（避免 LiveDisplay 排他锁干扰 Console.ReadKey）

using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public static class CommandPickerModal
{
    /// <summary>构建选择器面板。</summary>
    public static Panel BuildPicker(
        string filter,
        List<SlashCommands.SuggestionItem> items,
        int selectedIdx)
    {
        var rows = new List<IRenderable>();

        // ── 过滤输入行 ──
        var inputDisplay = string.IsNullOrEmpty(filter)
            ? "[yellow]/[/][dim] 输入过滤关键字...[/]"
            : $"[yellow]/{filter}[/]";
        rows.Add(new Markup(inputDisplay));

        // ── 帮助提示 ──
        rows.Add(new Markup("[dim]↑↓/jk 选择  Tab 补全  Enter 执行  Esc 取消[/]"));
        rows.Add(new Text(""));

        // ── 命令列表 ──
        if (items.Count == 0)
        {
            rows.Add(new Markup("[dim]无匹配命令[/]"));
        }
        else
        {
            var grouped = items
                .GroupBy(i => i.Group)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                rows.Add(new Markup($"[bold]{group.Key.EscapeMarkup()}[/]"));

                foreach (var item in group)
                {
                    var isSelected = item == items[selectedIdx];
                    var display = item.DisplayText.EscapeMarkup();
                    var prefix = isSelected ? "  [black on cyan] " : "  ";
                    var suffix = isSelected ? " [/]" : "";
                    var line = $"{prefix}{display}{suffix}";

                    if (item.IsAlias && !isSelected)
                        line = $"  [italic]{display}[/]";

                    rows.Add(new Markup(line));
                }

                rows.Add(new Text("")); // 组间空行
            }

            // 底部统计
            var aliasCount = items.Count(i => i.IsAlias);
            var cmdCount = items.Count - aliasCount;
            rows.Add(new Markup(
                $"[dim]{cmdCount} 个命令, {aliasCount} 个别名[/]"));
        }

        return new Panel(new Rows(rows.ToArray()))
        {
            Header = new PanelHeader("[bold yellow]📋 LTAI 命令[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1, 2, 1),
            Expand = true,
        };
    }

}
