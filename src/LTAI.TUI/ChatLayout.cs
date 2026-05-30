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

            // Streaming response (ESC 取消)
            var content = new StringBuilder();
            var statusLine = "";
            var hasFirstToken = false;
            var done = false;
            var cts = new CancellationTokenSource();

            // 后台监控 ESC 按键（每 100ms 检测，独立于流式循环）
            var escTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(true);
                            if (key.Key == ConsoleKey.Escape)
                            {
                                cts.Cancel();
                                return;
                            }
                        }
                        await Task.Delay(100, cts.Token);
                    }
                }
                catch (OperationCanceledException) { /* 正常取消 */ }
            }, cts.Token);

            await AnsiConsole.Live(new Panel("").BorderColor(Color.Yellow))
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    var animTask = AnimateAsync(ctx, content, statusLine, hasFirstToken, () => done);

                    try
                    {
                        await foreach (var update in _chat.ChatStreamingAsync(input).WithCancellation(cts.Token))
                        {
                            if (cts.Token.IsCancellationRequested)
                            {
                                statusLine = "[red]⏹ 用户取消[/]";
                                break;
                            }

                            var token = update.Text ?? "";
                            if (string.IsNullOrEmpty(token))
                            {
                                if (!hasFirstToken) statusLine = "🛠 调用工具中...";
                                continue;
                            }
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
                            hasFirstToken = true;
                            content.Append(token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        statusLine = "[red]⏹ 用户取消[/]";
                    }

                    done = true;
                    await Task.WhenAny(animTask, Task.Delay(500));
                    ctx.UpdateTarget(new Text(""));
                    ctx.Refresh();
                    var rawText = content.ToString();
                    if (rawText.Length > 0)
                        RenderMarkdown(rawText);
                    else
                        AnsiConsole.MarkupLine("[red]⏹ 已取消[/]");
                });

            var response = content.ToString();
            _history.Add(("assistant", response));
            if (string.IsNullOrWhiteSpace(response)) continue;

            // Response already shown in Live panel above — no need to re-render
            // Only handle plan detection and non-streaming actions
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

    /// <summary>在输入框上方显示一行统计（底部区域，不滚动掉）。</summary>
    private static void ShowStatsLine()
    {
        var requests = LTAI.Core.Configuration.UsageTracker.Requests;
        if (requests == 0)
        {
            AnsiConsole.MarkupLine("[grey]等待首次请求...  输入消息开始对话[/]");
            return;
        }

        var modelName = LTAI.Core.Configuration.UsageTracker.ActiveModel;
        var ctxText = LTAI.Core.Configuration.UsageTracker.ContextText();
        var ctxPct = LTAI.Core.Configuration.UsageTracker.ContextRatio();
        var barLen = 10;
        var filled = (int)(ctxPct * barLen);
        var bar = new string('▓', filled).PadRight(barLen, '░');
        var balance = LTAI.Core.Configuration.UsageTracker.BalanceDisplay;

        var stats = $"[grey]模型:[/] {modelName.EscapeMarkup()}  " +
                    $"[grey]上下文:[/] {bar} [grey]{ctxText.EscapeMarkup()}[/]  " +
                    $"[grey]Token:[/] {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}  " +
                    $"[grey]费用:[/] {LTAI.Core.Configuration.UsageTracker.CostDisplay.EscapeMarkup()}  " +
                    $"[grey]余额:[/] {balance.EscapeMarkup()}  " +
                    $"[grey]缓存:[/] {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%  " +
                    $"[grey]请求:[/] {requests}";
        AnsiConsole.MarkupLine(stats);
    }

    // 输入历史
    private static readonly List<string> _inputHistory = new();
    private static int _histIdx = -1;

    /// <summary>在底部显示输入框 + 统计（固定位置，不引发滚动）。</summary>
    private static string PromptAtBottom()
    {
        var y = Console.WindowHeight - 1;
        var statsY = Math.Max(0, y - 1);

        // 统计行（固定位置，不影响聊天内容）
        Console.SetCursorPosition(0, statsY);
        Console.Write(new string(' ', Console.WindowWidth - 1));
        Console.SetCursorPosition(0, statsY);
        var requests = LTAI.Core.Configuration.UsageTracker.Requests;
        if (requests > 0)
        {
            var m = LTAI.Core.Configuration.UsageTracker.ActiveModel.EscapeMarkup();
            var c = LTAI.Core.Configuration.UsageTracker.ContextText();
            var p = LTAI.Core.Configuration.UsageTracker.ContextRatio();
            var bar = new string('▓', (int)(p * 10)).PadRight(10, '░');
            var b = LTAI.Core.Configuration.UsageTracker.BalanceDisplay.EscapeMarkup();
            AnsiConsole.Markup(
                $"[grey]模型:[/] {m}  [grey]上下文:[/] {bar} [grey]{c}[/]  " +
                $"[grey]Token:[/] {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0}  " +
                $"[grey]费用:[/] {LTAI.Core.Configuration.UsageTracker.CostDisplay.EscapeMarkup()}  " +
                $"[grey]余额:[/] {b}  " +
                $"[grey]缓存:[/] {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%  " +
                $"[grey]请求:[/] {requests}");
        }

        // TextPrompt 编辑（成熟组件）
        Console.SetCursorPosition(0, y);
        var prompt = new TextPrompt<string>("[grey]>[/] ")
            .AllowEmpty();
        if (_inputHistory.Count > 0)
        {
            // 仅当 ↑ 键按下时填入历史（ReadKey 快速检测，不循环）
            if (Console.KeyAvailable)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.UpArrow)
                {
                    _histIdx = _inputHistory.Count - 1;
                    prompt.DefaultValue(_inputHistory[_histIdx]);
                }
            }
        }
        var input = AnsiConsole.Prompt(prompt);

        if (!string.IsNullOrWhiteSpace(input))
            _inputHistory.Add(input);
        if (input?.Trim() == "/")
            input = SlashCommands.ShowPicker();

        return input ?? "";
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
    /// <summary>
    /// Syntax highlighting keyword sets per language (ported from Desktop MarkdownRenderer).
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> CodeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = new() { "class", "struct", "interface", "enum", "record", "namespace", "using",
            "public", "private", "protected", "internal", "static", "readonly", "virtual", "abstract",
            "override", "async", "await", "new", "return", "if", "else", "for", "foreach", "while",
            "do", "switch", "case", "break", "continue", "try", "catch", "finally", "throw",
            "var", "void", "int", "string", "bool", "double", "float", "long", "char", "object",
            "true", "false", "null", "this", "base", "in", "out", "ref", "is", "as", "typeof",
            "get", "set", "value", "where", "select", "from" },
        ["python"] = new() { "class", "def", "return", "if", "elif", "else", "for", "while",
            "try", "except", "finally", "import", "from", "as", "with", "yield", "lambda",
            "True", "False", "None", "self", "and", "or", "not", "in", "is", "async", "await",
            "raise", "pass", "break", "continue", "global", "nonlocal" },
        ["javascript"] = new() { "function", "class", "const", "let", "var", "return", "if", "else",
            "for", "while", "do", "switch", "case", "break", "continue", "try", "catch", "finally",
            "throw", "new", "this", "async", "await", "import", "export", "default", "from",
            "true", "false", "null", "undefined" },
        ["bash"] = new() { "if", "then", "else", "elif", "fi", "for", "while", "do", "done",
            "case", "esac", "function", "return", "exit", "export", "source", "echo", "read",
            "set", "unset", "declare", "local" },
        ["go"] = new() { "func", "return", "if", "else", "for", "range", "switch", "case",
            "break", "continue", "go", "defer", "select", "chan", "map", "struct", "interface",
            "type", "package", "import", "var", "const", "nil", "true", "false" },
        ["rust"] = new() { "fn", "let", "mut", "return", "if", "else", "for", "while", "loop",
            "match", "break", "continue", "struct", "enum", "impl", "trait", "pub", "use",
            "mod", "crate", "self", "super", "where", "as", "in", "ref", "move", "async",
            "await", "true", "false", "Some", "None", "Ok", "Err" },
        ["java"] = new() { "class", "interface", "enum", "extends", "implements", "public",
            "private", "protected", "static", "final", "abstract", "synchronized", "volatile",
            "return", "if", "else", "for", "while", "do", "switch", "case", "break", "continue",
            "try", "catch", "finally", "throw", "throws", "new", "this", "super", "import",
            "package", "null", "true", "false", "void", "int", "long", "double", "float",
            "boolean", "char", "String", "var" },
    };

    /// <summary>
    /// Highlight a single line of code using keyword sets.
    /// Returns Spectre.Console markup string.
    /// </summary>
    private static string HighlightCodeLine(string line, HashSet<string>? keywords)
    {
        if (keywords == null || string.IsNullOrWhiteSpace(line))
            return line.EscapeMarkup();

        // Tokenize: split on word boundaries, preserve whitespace/punctuation
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsLetterOrDigit(line[i]) || line[i] == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                if (keywords.Contains(word))
                    result.Append($"[cyan]{word.EscapeMarkup()}[/]");
                else
                    result.Append(word.EscapeMarkup());
            }
            else
            {
                // String literals
                if (line[i] == '"' || line[i] == '\'')
                {
                    var quote = line[i];
                    result.Append("[green]");
                    result.Append(quote);
                    i++;
                    while (i < line.Length && line[i] != quote)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length)
                        { result.Append(line[i++]); }
                        result.Append(line[i++]);
                    }
                    if (i < line.Length) result.Append(line[i++]);
                    result.Append("[/]");
                }
                // Comments
                else if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    result.Append($"[grey]{line[i..].EscapeMarkup()}[/]");
                    break;
                }
                else if (line[i] == '#')
                {
                    result.Append($"[grey]{line[i..].EscapeMarkup()}[/]");
                    break;
                }
                // Numbers
                else if (char.IsDigit(line[i]))
                {
                    var start = i;
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'f' || line[i] == 'L' || line[i] == 'd'))
                        i++;
                    result.Append($"[yellow]{line[start..i].EscapeMarkup()}[/]");
                }
                else
                {
                    result.Append(line[i].ToString().EscapeMarkup());
                    i++;
                }
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Render markdown: code blocks with syntax highlighting, inline formatting, tables, lists.
    /// </summary>
    private static void RenderMarkdown(string text)
    {
        var blocks = text.Split("```", StringSplitOptions.None);
        for (int bi = 0; bi < blocks.Length; bi++)
        {
            if (bi % 2 == 1)
            {
                // Code block with syntax highlighting
                var codeLines = blocks[bi].Split('\n');
                var lang = codeLines[0].Trim().ToLowerInvariant();
                var lines = codeLines.Skip(1).ToArray();
                CodeKeywords.TryGetValue(lang, out var keywords);
                var pad = lines.Length.ToString().Length;
                var sb = new StringBuilder();
                for (int ln = 0; ln < lines.Length; ln++)
                {
                    var highlighted = HighlightCodeLine(lines[ln], keywords);
                    sb.AppendLine($"[grey]{(ln + 1).ToString().PadLeft(pad)}[/] {highlighted}");
                }
                var header = string.IsNullOrEmpty(lang) ? "code" : lang;
                AnsiConsole.Write(new Panel(sb.ToString().TrimEnd()).Header($"[yellow]{header.EscapeMarkup()}[/]").BorderColor(Color.Blue));
            }
            else if (!string.IsNullOrWhiteSpace(blocks[bi]))
            {
                // Text block
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
        text = text.Replace("[", "[[").Replace("]", "]]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", m => $"[bold]{m.Groups[1].Value}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__(.+?)__", m => $"[bold]{m.Groups[1].Value}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*", m => $"[italic]{m.Groups[1].Value}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_(.+?)_", m => $"[italic]{m.Groups[1].Value}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"``(.+?)``|`(.+?)`", m => $"[grey]{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\[(.+?)\]\((.+?)\)", m => $"[link={m.Groups[2].Value}]{m.Groups[1].Value}[/]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"~~(.+?)~~", m => $"[strikethrough]{m.Groups[1].Value}[/]");
        return text;
    }

    /// <summary>
    /// Render a markdown table row.
    /// </summary>
    private static void RenderTableLine(string line)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\|[\s\-:]+\|")) return;
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
