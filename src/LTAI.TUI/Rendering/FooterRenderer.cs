using System.Text;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

// FooterRenderer references ChatLayout.CurrentViewName, but ChatLayout imports
// ThemeService via "using static ThemeService" — avoid circular dependency by
// using the full type name. Namespace is shared (LTAI.TUI).

namespace LTAI.TUI.Rendering;

public sealed class FooterRenderer
{
    private readonly IAnsiConsole _console;

    public FooterRenderer(IAnsiConsole console)
    {
        _console = console ?? AnsiConsole.Console;
    }

    public Panel BuildFooter(
        string pickerText,
        string statusText,
        bool isFirstEmpty,
        List<string> inputLines,
        int cursorLine,
        int cursorCol,
        int maxInputLines,
        List<SlashCommands.SuggestionItem>? suggestions = null,
        int selIdx = -1,
        string? startupMessage = null)
    {
        var renders = new List<IRenderable>();

        if (!string.IsNullOrEmpty(pickerText))
        {
            var cursorBlink = Environment.TickCount % 1000 < 530;
            var cursorTag = ThemeService.PrimaryTag;
            var cursor = cursorBlink ? $"[bold {cursorTag}]▌[/]" : " ";
            renders.Add(new Markup($"{cursor} {pickerText.EscapeMarkup()}"));

            if (suggestions != null && suggestions.Count > 0)
            {
                var displayed = suggestions.Take(6).ToList();
                var suggestionText = new StringBuilder();
                for (int i = 0; i < displayed.Count; i++)
                {
                    var s = displayed[i];
                    var isSelected = i == selIdx;
                    var cmd = s.Completion;
                    if (isSelected)
                        suggestionText.Append($"[black on {cursorTag}] {cmd,-12} [/]");
                    else
                        suggestionText.Append($" [{ThemeService.MutedTag}]{cmd,-12}[/]");
                }
                if (suggestions.Count > 6)
                    suggestionText.Append($" [{ThemeService.MutedTag}]... +{suggestions.Count - 6}[/]");
                renders.Add(new Markup(suggestionText.ToString().TrimStart()));
                renders.Add(new Markup($"[{ThemeService.MutedTag}]↑↓=选择  Tab=补全  Enter=执行  Esc=取消[/]"));
            }
        }
        else
        {
            var showWatermark = (inputLines.Count == 1 && inputLines[0].Length == 0) && isFirstEmpty;
            var cursorBlink = Environment.TickCount % 1000 < 530;

            if (showWatermark)
            {
                var cursorTag2 = ThemeService.PrimaryTag;
                var cursor = cursorBlink ? $"[bold {cursorTag2}]▌[/]" : " ";
                renders.Add(new Markup(
                    $"{cursor} [{ThemeService.MutedTag}] 输入消息  SEnter=发送  Enter=换行  ↑↓=光标  /开命令  Ctrl+↑↓=历史[/]"));
            }
            else
            {
                var visibleStart = Math.Max(0, cursorLine - maxInputLines + 1);
                var visibleLines = inputLines
                    .Skip(visibleStart)
                    .Take(maxInputLines)
                    .ToList();

                foreach (var (line, idx) in visibleLines.Select((l, i) => (l, i)))
                {
                    var lineNum = visibleStart + idx;
                    var isCursorLine = lineNum == cursorLine;
                    var cursorTag3 = ThemeService.PrimaryTag;
                    var prefix = isCursorLine && cursorBlink ? $"[bold {cursorTag3}]▌[/]" :
                                 isCursorLine ? $" [{ThemeService.MutedTag}]▌[/]" : "  ";
                    var colored = ChatRenderer.HighlightCommands(line.EscapeMarkup());
                    renders.Add(new Markup($"{prefix} {colored}"));
                }
            }
        }

        // 单行状态条
        var statusLine = BuildStatusLine(pickerText, statusText, startupMessage);
        renders.Add(new Markup(statusLine));

        return new Panel(new Rows(renders.ToArray()))
            .Border(BoxBorder.None)
            .Expand();
    }

    private static string BuildStatusLine(string pickerText, string statusText, string? startupMessage)
    {
        var viewName = ChatLayout.CurrentViewName;

        if (!string.IsNullOrEmpty(statusText))
            return $"[{ThemeService.MutedTag}]{statusText}[/]  [{ThemeService.MutedTag}]{viewName}[/]";

        var r = UsageTracker.Requests;
        if (r > 0)
        {
            var sep = $" [{ThemeService.MutedTag}]·[/] ";
            var sb = new StringBuilder();
            var m = UsageTracker.ActiveModel.EscapeMarkup();
            sb.Append($"[bold]{m}[/]{sep}");
            sb.Append($"Token: {UsageTracker.TotalTokens:N0}{sep}");
            sb.Append($"费用: {UsageTracker.CostDisplay.EscapeMarkup()}{sep}");
            sb.Append($"余额: {UsageTracker.BalanceDisplay.EscapeMarkup()}{sep}");
            sb.Append($"缓存: {UsageTracker.CacheHitRate:F1}%");

            // 上下文仅超阈值时显示
            var ctxText = UsageTracker.ContextText();
            if (!string.IsNullOrEmpty(ctxText))
            {
                var ratio = UsageTracker.ContextRatio();
                if (ratio > 0.75)
                    sb.Append($"{sep}[{ThemeService.ErrorTag}]上下文: {ctxText.EscapeMarkup()} ⚠[/]");
            }

            sb.Append($"  [{ThemeService.PrimaryTag}]{viewName}[/]");
            return sb.ToString();
        }

        return startupMessage != null
            ? $"[{ThemeService.WarningTag}]⚠️ {startupMessage.EscapeMarkup()}[/]  [{ThemeService.MutedTag}]{viewName}[/]"
            : $"[{ThemeService.MutedTag}]等待首次请求...  输入消息开始对话[/]  [{ThemeService.PrimaryTag}]{viewName}[/]";
    }
}
