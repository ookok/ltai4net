// 命令选择器 — 在 Live Display 的 Messages 面板中渲染
// 使用 Layout 的 Messages 区域展示命令列表，所有交互在 Live 内部完成。

using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

/// <summary>
/// 命令选择器 — 在 Live Display 内的 Messages 面板渲染交互式命令列表。
/// </summary>
public static class CommandPickerModal
{
    /// <summary>
    /// 在 Layout 的 Messages 面板内显示命令选择器。
    /// </summary>
    /// <param name="layout">当前 ChatLayout 的 Layout 实例。</param>
    /// <param name="ctx">Live 显示上下文，用于刷新。</param>
    /// <returns>选中命令字符串（如 "/model"），取消返回 null。</returns>
    public static string? Show(Layout layout, LiveDisplayContext ctx)
    {
        var filter = new StringBuilder();
        var items = SlashCommands.GetSuggestionItems("/");
        var selectedIdx = items.Count > 0 ? 0 : -1;

        // 初始渲染
        layout["Messages"].Update(BuildPicker(filter, items, selectedIdx));
        ctx.Refresh();

        while (true)
        {
            var key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    return null;

                case ConsoleKey.UpArrow:
                    if (items.Count > 0)
                        selectedIdx = (selectedIdx - 1 + items.Count) % items.Count;
                    break;

                case ConsoleKey.DownArrow:
                    if (items.Count > 0)
                        selectedIdx = (selectedIdx + 1) % items.Count;
                    break;

                case ConsoleKey.Tab:
                {
                    var completions = items
                        .Select(s => s.Completion)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (completions.Count == 1)
                    {
                        return completions[0] + " ";
                    }

                    if (completions.Count > 1)
                    {
                        var lcp = LongestCommonPrefix(completions);
                        if (lcp.Length > ("/" + filter).Length)
                        {
                            filter.Clear();
                            filter.Append(lcp.Length > 1 ? lcp[1..] : "");
                        }
                    }
                    break;
                }

                case ConsoleKey.Enter:
                {
                    if (selectedIdx >= 0 && selectedIdx < items.Count)
                        return items[selectedIdx].Completion;

                    var raw = "/" + filter.ToString().Trim();
                    return string.IsNullOrWhiteSpace(raw) || raw == "/" ? null : raw;
                }

                case ConsoleKey.Backspace:
                    if (filter.Length > 0)
                        filter.Length--;
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                        filter.Append(key.KeyChar);
                    break;
            }

            // 重新计算建议
            var prefix = "/" + filter;
            items = prefix.Length > 1
                ? SlashCommands.GetSuggestionItems(prefix)
                : SlashCommands.GetSuggestionItems("/");
            if (selectedIdx >= items.Count) selectedIdx = items.Count - 1;
            if (selectedIdx < 0 && items.Count > 0) selectedIdx = 0;

            // 刷新 Messages 面板
            layout["Messages"].Update(BuildPicker(filter, items, selectedIdx));
            ctx.Refresh();
        }
    }

    private static Panel BuildPicker(
        StringBuilder filter,
        List<SlashCommands.SuggestionItem> items,
        int selectedIdx)
    {
        var rows = new List<IRenderable>();

        // ── 过滤输入行 ──
        var inputDisplay = string.IsNullOrEmpty(filter.ToString())
            ? "[yellow]/[/][dim] 输入过滤关键字...[/]"
            : $"[yellow]/{filter}[/]";
        rows.Add(new Markup(inputDisplay));

        // ── 帮助提示 ──
        rows.Add(new Markup("[dim]↑↓ 选择  Tab 补全  Enter 执行  Esc 取消[/]"));
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
                rows.Add(new Markup($"[bold]{group.Key}[/]"));

                foreach (var item in group)
                {
                    var isSelected = item == items[selectedIdx];
                    var prefix = isSelected ? "  [reverse] " : "  ";
                    var suffix = isSelected ? " [/]" : "";
                    var line = $"{prefix}{item.DisplayText}{suffix}";

                    if (item.IsAlias && !isSelected)
                        line = $"  [italic]{item.DisplayText}[/]";

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

    private static string LongestCommonPrefix(List<string> strings)
    {
        if (strings.Count == 0) return "";
        if (strings.Count == 1) return strings[0];

        var first = strings[0];
        for (int i = 0; i < first.Length; i++)
        {
            for (int j = 1; j < strings.Count; j++)
            {
                if (i >= strings[j].Length || strings[j][i] != first[i])
                    return first[..i];
            }
        }
        return first;
    }
}
