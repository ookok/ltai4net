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
        AnsiConsole.MarkupLine("[bold]Chat — type your message, empty line to return[/]");

        while (true)
        {
            var input = AnsiConsole.Ask<string>("[grey]>[/]");
            if (string.IsNullOrEmpty(input)) return;

            _history.Add(("user", input));

            // Check for slash commands
            var cmdStatus = "";
            if (SlashCommands.TryExecute(input, ref _running, ref cmdStatus))
            {
                if (!string.IsNullOrEmpty(cmdStatus))
                    AnsiConsole.MarkupLine(cmdStatus);
                if (!_running) return; // /exit triggered
                continue; // don't send to agent
            }

            var content = new StringBuilder();
            var statusLine = "";
            var hasFirstToken = false;
            var isComplete = false;
            var frameIdx = 0;

            await AnsiConsole.Live(new Panel("")
                .Header("[yellow]LTAI ⚪[/]")
                .BorderColor(Color.Yellow))
            .StartAsync(async ctx =>
            {
                var animTask = Task.Run(async () =>
                {
                    while (!isComplete)
                    {
                        await Task.Delay(250);
                        if (isComplete) break;
                        frameIdx++;
                        var dot = DotFrames[frameIdx % 3];
                        var status = !hasFirstToken
                            ? $"{dot} [yellow]Thinking...[/]"
                            : $"{dot} [green]Processing...[/]";
                        if (!string.IsNullOrEmpty(statusLine))
                            status += $"\n[grey]{statusLine}[/]";
                        ctx.UpdateTarget(
                            new Panel(content.Length > 0 ? $"{content}\n\n[grey]{status}[/]" : status)
                                .Header($"[yellow]LTAI {dot}[/]").BorderColor(Color.Yellow));
                        ctx.Refresh();
                    }
                });

                await foreach (var update in _chat.ChatStreamingAsync(input))
                {
                    var token = update.Text ?? "";
                    if (TryParseToolResult(token, out var parsed))
                    {
                        statusLine = parsed.success ? $":check_mark: {Truncate(parsed.output, 60)}" : $":cross_mark: [red]{parsed.error.EscapeMarkup()}[/]";
                        continue;
                    }
                    if (token.StartsWith("HANDOFF TO ")) { statusLine = $":arrow_right: [yellow]{token.EscapeMarkup()}[/]"; continue; }
                    if (token.StartsWith("[budget:") || token.StartsWith("[note:")) { statusLine = $":information: [grey]{token.EscapeMarkup()}[/]"; continue; }
                    if (!string.IsNullOrWhiteSpace(token)) hasFirstToken = true;
                    content.Append(token);
                }
                isComplete = true;
                ctx.UpdateTarget(
                    new Panel(content.Length > 0
                        ? $"{content}{(string.IsNullOrEmpty(statusLine) ? "" : $"\n\n[grey]{statusLine}[/]")}"
                        : "[red]No response[/]")
                        .Header("[yellow]LTAI ✅[/]").BorderColor(Color.Green));
                ctx.Refresh();
            });

            var response = content.ToString();
            _history.Add(("assistant", response));
            if (string.IsNullOrWhiteSpace(response)) continue;

            // Diff or markdown rendering
            if (IsDiffContent(response))
                RenderDiffBlock(response);
            else
                RenderMarkdown(response);
        }
    }

    private static void RenderMarkdown(string text)
    {
        var blocks = text.Split("```", StringSplitOptions.None);
        for (int bi = 0; bi < blocks.Length; bi++)
        {
            if (bi % 2 == 1)
            {
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
                foreach (var para in blocks[bi].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (para.StartsWith("# ")) AnsiConsole.MarkupLine($"[bold yellow]{para[2..].EscapeMarkup()}[/]");
                    else if (para.StartsWith("## ")) AnsiConsole.MarkupLine($"[bold]{para[3..].EscapeMarkup()}[/]");
                    else if (para.StartsWith("- ") || para.StartsWith("* ")) AnsiConsole.MarkupLine($"  [green]•[/] {para[2..].EscapeMarkup()}");
                    else AnsiConsole.MarkupLine(para.EscapeMarkup());
                }
            }
        }
    }

    // ─── Diff detection & rendering ───

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
