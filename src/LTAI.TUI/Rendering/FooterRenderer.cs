using System.Text;
using LTAI.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Rendering;

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
        var r = UsageTracker.Requests;

        if (r > 0)
        {
            var m = UsageTracker.ActiveModel.EscapeMarkup();
            var b = UsageTracker.BalanceDisplay.EscapeMarkup();
            var tps = UsageTracker.TpsDisplay;
            var tc = UsageTracker.ToolCalls;
            var saved = UsageTracker.CacheSavedDisplay;

            renders.Add(new Markup(
                $"[bold]{m}[/]  [grey]·[/]  Token: {UsageTracker.TotalTokens:N0}" +
                $"  [grey]·[/]  费用: {UsageTracker.CostDisplay.EscapeMarkup()}" +
                (string.IsNullOrEmpty(tps) ? "" : $"  [grey]·[/]  {tps}") +
                $"  [grey]·[/]  请求: {r}"));

            renders.Add(new Markup(
                $"余额: {b}  [grey]·[/]  缓存: {UsageTracker.CacheHitRate:F1}%" +
                (tc > 0 ? $"  [grey]·[/]  工具: {tc}次" : "") +
                (saved != "¥0.0000" ? $"  [grey]·[/]  节省: {saved}" : "")));

            var ctxText = UsageTracker.ContextText();
            if (!string.IsNullOrEmpty(ctxText))
            {
                var ratio = UsageTracker.ContextRatio();
                var ctxColor = ratio < 0.5 ? "grey" : ratio < 0.75 ? "yellow" : "red";
                renders.Add(new Markup(
                    $"[{ctxColor}]上下文:[/] {ctxText.EscapeMarkup()}" +
                    (ratio > 0.75 ? "  [red]⚠ 即将压缩[/]" : "")));
            }
        }
        else
        {
            var msg = startupMessage;
            if (msg != null)
            {
                renders.Add(new Markup($"[yellow]⚠️ {msg.EscapeMarkup()}[/]"));
                renders.Add(new Markup("[grey]等待首次请求...  输入消息开始对话[/]"));
            }
            else
            {
                renders.Add(new Markup("[grey]等待首次请求...  输入消息开始对话[/]"));
            }
        }

        if (!string.IsNullOrEmpty(statusText))
            renders.Add(new Markup(statusText));

        if (!string.IsNullOrEmpty(pickerText))
        {
            var cursorBlink = Environment.TickCount % 1000 < 530;
            var cursor = cursorBlink ? "[bold deepskyblue1]▌[/]" : " ";
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
                        suggestionText.Append($"[black on cyan] {cmd,-12} [/]");
                    else
                        suggestionText.Append($" [grey]{cmd,-12}[/]");
                }
                if (suggestions.Count > 6)
                    suggestionText.Append($" [dim]... +{suggestions.Count - 6}[/]");
                renders.Add(new Markup(suggestionText.ToString().TrimStart()));
                renders.Add(new Markup("[dim]↑↓=选择  Tab=补全  Enter=执行  Esc=取消[/]"));
            }
        }
        else
        {
            var showWatermark = (inputLines.Count == 1 && inputLines[0].Length == 0) && isFirstEmpty;
            var cursorBlink = Environment.TickCount % 1000 < 530;

            if (showWatermark)
            {
                var cursor = cursorBlink ? "[bold deepskyblue1]▌[/]" : " ";
                renders.Add(new Markup(
                    $"{cursor} [dim]│[/][grey] 输入消息  SEnter=发送  Enter=换行  ↑↓=光标  /开命令  Ctrl+↑↓=历史  /[/]"));
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
                    var prefix = isCursorLine && cursorBlink ? "[bold deepskyblue1]▌[/]" :
                                 isCursorLine ? " [grey]▌[/]" : "  ";
                    var colored = ChatRenderer.HighlightCommands(line.EscapeMarkup());
                    renders.Add(new Markup($"{prefix} {colored}"));
                }
            }
        }

        return new Panel(new Rows(renders.ToArray()))
            .Border(BoxBorder.None)
            .Expand();
    }
}
