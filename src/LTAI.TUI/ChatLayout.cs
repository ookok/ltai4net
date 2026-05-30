using System.Text;
using System.Text.Json;
using LTAI.Agent;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class ChatLayout
{
    private readonly ChatAgent _chat;
    private readonly List<(string role, string content)> _history = new();
    private static readonly string[] DotFrames = ["⚪", "⚫", "⚪"];
    private bool _running = true;

    public ChatLayout(ChatAgent chat) => _chat = chat;

    public async Task RenderAsync()
    {
        Console.Clear();
        RenderHeader();

        while (true)
        {
            // Input line: always at the bottom of the visible area
            var input = PromptAtBottom();
            if (string.IsNullOrEmpty(input)) return;

            _history.Add(("user", input));
            RenderUserMsg(input);

            // Check for slash commands
            var cmdStatus = "";
            if (SlashCommands.TryExecute(input, ref _running, ref cmdStatus))
            {
                if (!string.IsNullOrEmpty(cmdStatus))
                    AnsiConsole.MarkupLine(cmdStatus);
                if (!_running) return;
                continue;
            }

            // Streaming response
            var content = new StringBuilder();
            var statusLine = "";
            var hasFirstToken = false;
            var done = false;

            await AnsiConsole.Live(new Panel("").BorderColor(Color.Yellow))
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
#pragma warning disable CS1998 // async without await — fine as fire-and-forget animation
                    var animTask = AnimateAsync(ctx, content, statusLine, hasFirstToken, () => done);
#pragma warning restore CS1998

                    await foreach (var update in _chat.ChatStreamingAsync(input))
                    {
                        var token = update.Text ?? "";
                        if (TryParseToolResult(token, out var parsed))
                        {
                            statusLine = parsed.success
                                ? $"✓ {Truncate(parsed.output, 60)}"
                                : $"✗ [red]{parsed.error.EscapeMarkup()}[/]";
                            continue;
                        }
                        if (token.StartsWith("HANDOFF TO "))
                        { statusLine = $"→ [yellow]{token.EscapeMarkup()}[/]"; continue; }
                        if (token.StartsWith("[budget:") || token.StartsWith("[note:"))
                        { statusLine = $"[grey]{token.EscapeMarkup()}[/]"; continue; }
                        if (!string.IsNullOrWhiteSpace(token)) hasFirstToken = true;
                        content.Append(token);
                    }
                    done = true;
                    ctx.UpdateTarget(new Panel(content.Length > 0 ? content.ToString() : "[red]No response[/]")
                        .BorderColor(Color.Green));
                    ctx.Refresh();
                });

            var response = content.ToString();
            _history.Add(("assistant", response));
            if (string.IsNullOrWhiteSpace(response)) continue;

            // Render response + plan detection
            if (IsDiffContent(response)) RenderDiffBlock(response);
            else RenderMarkdown(response);

            if (response.Contains("## Plan:") || response.Contains("approve"))
            {
                var ps = LTAI.Agent.Tools.PlanTools.PlanStatus();
                if (!ps.Contains("No active plan"))
                {
                    AnsiConsole.MarkupLine("\n[bold yellow]📋 输入 [cyan]/approve[/] 批准执行计划[/]");
                }
            }
        }
    }

    /// <summary>Keep input prompt at the last visible line of the terminal.</summary>
    private static string PromptAtBottom()
    {
        var y = Console.WindowHeight - 1;
        Console.SetCursorPosition(0, y);
        Console.Write(new string(' ', Console.WindowWidth - 1));
        Console.SetCursorPosition(0, y);
        AnsiConsole.Markup("[grey]>[/] ");
        return Console.ReadLine() ?? "";
    }

    private static void RenderHeader()
    {
        Console.SetCursorPosition(0, 0);
        AnsiConsole.MarkupLine("[bold]LTAI 聊天[/] — [grey]输入空行返回[/]");
        AnsiConsole.MarkupLine("[dim]─[/]" + new string('─', Console.WindowWidth - 2));
    }

    private void RenderUserMsg(string msg)
    {
        var y = Console.CursorTop;
        if (y >= Console.WindowHeight - 2)
        {
            // Scroll by clearing and redrawing header
            Console.Clear();
            RenderHeader();
        }
        AnsiConsole.MarkupLine($"[cyan]你:[/] {msg.EscapeMarkup()}");
    }

    private async Task AnimateAsync(LiveDisplayContext ctx, StringBuilder content,
        string statusLine, bool hasFirstToken, Func<bool> isDone)
    {
        var frameIdx = 0;
        while (!isDone())
        {
            await Task.Delay(250);
            frameIdx++;
            var dot = DotFrames[frameIdx % 3];
            var status = !hasFirstToken
                ? $"{dot} 思考中..."
                : $"{dot} 处理中...";
            if (!string.IsNullOrEmpty(statusLine))
                status += $"\n[grey]{statusLine}[/]";
            var display = content.Length > 0
                ? $"{content}\n\n[grey]{status}[/]"
                : $"[grey]{status}[/]";
            ctx.UpdateTarget(new Panel(display).BorderColor(Color.Yellow));
            ctx.Refresh();
        }
    }

    /// <summary>
    /// Render markdown text to Spectre.Console markup.
    /// Supports: bold, italic, inline code, links, headers, lists, tables, code blocks.
    /// </summary>
    private static void RenderMarkdown(string text)
    {
        var blocks = text.Split("```", StringSplitOptions.None);
        for (int bi = 0; bi < blocks.Length; bi++)
        {
            if (bi % 2 == 1)
            {
                // Code block
                var codeLines = blocks[bi].Split('\n');
                var lang = codeLines[0].Trim();
                var lines = codeLines.Skip(1).ToArray();
                var pad = lines.Length.ToString().Length;
                var sb = new StringBuilder();
                for (int ln = 0; ln < lines.Length; ln++)
                    sb.AppendLine($"[grey]{(ln + 1).ToString().PadLeft(pad)}[/] {lines[ln].EscapeMarkup()}");
                AnsiConsole.Write(new Panel(sb.ToString().TrimEnd()).Header($"[yellow]{lang}[/]").BorderColor(Color.Blue));
            }
            else if (!string.IsNullOrWhiteSpace(blocks[bi]))
            {
                // Text block — convert markdown inline formatting to Spectre markup
                foreach (var line in blocks[bi].Split('\n'))
                {
                    var trimmed = line.TrimEnd();
                    if (string.IsNullOrWhiteSpace(trimmed)) { AnsiConsole.WriteLine(); continue; }

                    if (trimmed.StartsWith("# "))
                        AnsiConsole.MarkupLine($"[bold yellow]{MdToSpectre(trimmed[2..])}[/]");
                    else if (trimmed.StartsWith("## "))
                        AnsiConsole.MarkupLine($"[bold]{MdToSpectre(trimmed[3..])}[/]");
                    else if (trimmed.StartsWith("### "))
                        AnsiConsole.MarkupLine($"[bold cyan]{MdToSpectre(trimmed[4..])}[/]");
                    else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                        AnsiConsole.MarkupLine($"  [green]•[/] {MdToSpectre(trimmed[2..])}");
                    else if (trimmed.StartsWith("1. ") || trimmed.StartsWith("2. ") || trimmed.StartsWith("3. "))
                        AnsiConsole.MarkupLine($"  [grey]{trimmed[..3]}[/]{MdToSpectre(trimmed[3..])}");
                    else if (trimmed.StartsWith("|") && trimmed.EndsWith("|"))
                        RenderTableLine(trimmed);
                    else if (trimmed.StartsWith("> "))
                        AnsiConsole.MarkupLine($"  [grey]│[/] [italic]{MdToSpectre(trimmed[2..])}[/]");
                    else
                        AnsiConsole.MarkupLine(MdToSpectre(trimmed));
                }
            }
        }
    }

    /// <summary>
    /// Convert markdown inline formatting to Spectre.Console markup.
    /// </summary>
    private static string MdToSpectre(string text)
    {
        // Escape existing Spectre markup brackets FIRST
        text = text.Replace("[", "[[").Replace("]", "]]");

        // Bold: **text** or __text__ → [bold]text[/]
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\*\*(.+?)\*\*", m => $"[bold]{m.Groups[1].Value}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"__(.+?)__", m => $"[bold]{m.Groups[1].Value}[/]");

        // Italic: *text* or _text_ → [italic]text[/]
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\*(.+?)\*", m => $"[italic]{m.Groups[1].Value}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"_(.+?)_", m => $"[italic]{m.Groups[1].Value}[/]");

        // Inline code: `code` → [grey]code[/]
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"``(.+?)``|`(.+?)`", m => $"[grey]{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}[/]");

        // Links: [text](url) → [link=url]text[/]
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\[(.+?)\]\((.+?)\)", m => $"[link={m.Groups[2].Value}]{m.Groups[1].Value}[/]");

        // Strikethrough: ~~text~~ → [strikethrough]text[/]
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"~~(.+?)~~", m => $"[strikethrough]{m.Groups[1].Value}[/]");

        return text;
    }

    /// <summary>
    /// Render a markdown table row. Very basic — just pipes + alignment.
    /// </summary>
    private static void RenderTableLine(string line)
    {
        // Skip separator rows (|---|---|)
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\|[\s\-:]+\|"))
            return;

        var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var formatted = string.Join(" [grey]│[/] ", cells.Select(c => MdToSpectre(c.Trim())));
        AnsiConsole.MarkupLine($" [grey]│[/] {formatted} [grey]│[/]");
    }

    private static bool IsDiffContent(string text)
    {
        var lines = text.Split('\n');
        return lines.Count(l => l.StartsWith("--- ") || l.StartsWith("+++ ") || l.StartsWith("@@ ")) >= 2;
    }

    private static void RenderDiffBlock(string diff)
    {
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- ") || line.StartsWith("+++ "))
                AnsiConsole.MarkupLine($"[cyan]{line.EscapeMarkup()}[/]");
            else if (line.StartsWith("@@ "))
                AnsiConsole.MarkupLine($"[green]{line.EscapeMarkup()}[/]");
            else if (line.StartsWith("+") && !line.StartsWith("+++"))
                AnsiConsole.MarkupLine($"[lime]{line.EscapeMarkup()}[/]");
            else if (line.StartsWith("-") && !line.StartsWith("---"))
                AnsiConsole.MarkupLine($"[red]{line.EscapeMarkup()}[/]");
            else
                AnsiConsole.MarkupLine(line.EscapeMarkup());
        }
    }

    private static bool TryParseToolResult(string text, out (bool success, string output, string error) result)
    {
        result = default;
        text = text.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var s)) return false;
            var ok = s.GetBoolean();
            result = (ok, ok && root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                !ok && root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
            return true;
        }
        catch { return false; }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
