using System.Text;
using System.Text.RegularExpressions;
using LTAI.Core.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI.Rendering;

/// <summary>
/// Facade for the LTAI chat TUI rendering layer.
/// Delegates to <see cref="MessagePanelRenderer"/> and <see cref="FooterRenderer"/>.
/// </summary>
public sealed class ChatRenderer
{
    private readonly MessagePanelRenderer _messageRenderer;
    private readonly FooterRenderer _footerRenderer;

    public ChatRenderer(IAnsiConsole console)
    {
        _console = console ?? AnsiConsole.Console;
        _messageRenderer = new MessagePanelRenderer(_console);
        _footerRenderer = new FooterRenderer(_console);
    }

    private readonly IAnsiConsole _console;

    public async Task ShowInitStatusAsync(Func<StatusContext, Task> action, string initialMessage)
    {
        try { await _console.Status().Spinner(Spinner.Known.Dots).StartAsync(initialMessage, action).ConfigureAwait(false); }
        catch { }
    }

    public Panel BuildMessagePanel(string role, string rawContent, int historyIndex = -1,
        string? reasoning = null, HashSet<int>? expandedMessages = null)
        => _messageRenderer.BuildMessagePanel(role, rawContent, historyIndex, reasoning, expandedMessages);

    public Panel BuildCodeBlockPanel(string code, string? lang)
        => _messageRenderer.BuildCodeBlockPanel(code, lang);

    public Panel BuildMessagesPanel(
        List<(string role, IRenderable? rendered, string rawContent, string? reasoning)> history,
        string? streamingContent,
        List<(string name, string args, string result)>? toolCalls,
        int scrollOffset, int maxVisibleMessages, HashSet<int>? expandedMessages)
        => _messageRenderer.BuildMessagesPanel(history, streamingContent, toolCalls, scrollOffset, maxVisibleMessages, expandedMessages);

    public Panel BuildWelcomePanel()
        => _messageRenderer.BuildWelcomePanel();

    public Panel BuildFooter(
        string pickerText, string statusText, bool isFirstEmpty,
        List<string> inputLines, int cursorLine, int cursorCol, int maxInputLines,
        List<SlashCommands.SuggestionItem>? suggestions = null, int selIdx = -1,
        string? startupMessage = null)
        => _footerRenderer.BuildFooter(pickerText, statusText, isFirstEmpty, inputLines,
            cursorLine, cursorCol, maxInputLines, suggestions, selIdx, startupMessage);

    // Instance forwarding for static methods (used by ResponseStreamer via _renderer)
    public string RenderToolCallsAsTree(List<(string name, string args, string result)> calls) => RenderToolCallsAsTreeStatic(calls);

    // ── Static methods (shared across renderers) ──

    public static string MdToPanelContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        try
        {
            var doc = Markdig.Markdown.Parse(text);
            var spectreRenderer = new SpectreMarkdigRenderer();
            spectreRenderer.Render(doc);
            var result = spectreRenderer.ToString().TrimEnd();
            return result.Length > 0 ? result : "";
        }
        catch { return text.EscapeMarkup(); }
    }

    public static string RenderToolCallsAsTreeStatic(List<(string name, string args, string result)> calls)
    {
        var sb = new StringBuilder();
        foreach (var (name, args, result) in calls)
        {
            var a = Truncate(args, 40);
            sb.AppendLine($"[bold yellow]🔧 {name}[/]([grey]{a.EscapeMarkup()}[/])");
            var r = Truncate(result, 80);
            if (!string.IsNullOrEmpty(r))
                sb.AppendLine($"  [green]└─[/] {r.EscapeMarkup()}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string HighlightCommands(string escaped)
    {
        escaped = Regex.Replace(escaped, @"(^|\s)(/[a-zA-Z][\w-]*)",
            m => m.Groups[1].Value + "[bold yellow]" + m.Groups[2].Value + "[/]");
        escaped = Regex.Replace(escaped, @"(^|\s)(#[\w-]+)",
            m => m.Groups[1].Value + "[bold cyan]" + m.Groups[2].Value + "[/]");
        return escaped;
    }

    public static readonly string[] PulseFrames =
    [
        "[deepskyblue1]⠋[/]", "[deepskyblue1]⠙[/]", "[deepskyblue1]⠹[/]",
        "[deepskyblue1]⠸[/]", "[deepskyblue1]⠼[/]", "[deepskyblue1]⠴[/]",
        "[deepskyblue1]⠦[/]", "[deepskyblue1]⠧[/]", "[deepskyblue1]⠇[/]",
        "[deepskyblue1]⠏[/]",
    ];

#pragma warning disable IDE0051 // used by MessagePanelRenderer via ChatRenderer.MdToPanelContent
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
#pragma warning restore IDE0051
}
