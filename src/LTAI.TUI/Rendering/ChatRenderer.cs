using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Core.Rendering;
using LTAI.TUI.Services;
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

    public IRenderable BuildMessagePanel(string role, string rawContent, int historyIndex = -1,
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

    // ── Render cache ──
    private static readonly ConcurrentDictionary<int, string> _renderCache = new();
    private const int MaxRenderCacheEntries = 256;

    public static void ClearRenderCache() { _renderCache.Clear(); }

    public static string MdToPanelContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var hasImages = text.Contains("![") || text.Contains("[image:");
        var hash = text.GetHashCode();
        if (!hasImages && _renderCache.TryGetValue(hash, out var cached))
            return cached;
        try
        {
            var result = new SpectreMarkdigRenderer().RenderToString(text);
            if (result.Length == 0) return "";
            if (!hasImages && _renderCache.Count < MaxRenderCacheEntries)
                _renderCache[hash] = result;
            return result;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatRenderer] MdToPanelContent failed: {ex.Message}"); return text.EscapeMarkup(); }
    }

    public static string RenderToolCallsAsTreeStatic(List<(string name, string args, string result)> calls)
    {
        var sb = new StringBuilder();
        foreach (var (name, args, result) in calls)
        {
            var a = Truncate(args, 40);
            sb.AppendLine($"[bold {ThemeService.WarningTag}]🔧 {name}[/]([{ThemeService.MutedTag}]{a.EscapeMarkup()}[/])");
            var r = Truncate(result, 80);
            if (!string.IsNullOrEmpty(r))
                sb.AppendLine($"  [{ThemeService.AccentTag}]└─[/] {r.EscapeMarkup()}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string HighlightCommands(string escaped)
    {
        escaped = Regex.Replace(escaped, @"(^|\s)(/[a-zA-Z][\w-]*)",
            m => m.Groups[1].Value + $"[bold {ThemeService.WarningTag}]" + m.Groups[2].Value + "[/]");
        escaped = Regex.Replace(escaped, @"(^|\s)(#[\w-]+)",
            m => m.Groups[1].Value + $"[bold {ThemeService.PrimaryTag}]" + m.Groups[2].Value + "[/]");
        return escaped;
    }

    public static string[] PulseFrames => _pulseFrames[(ThemeService.IsLight ? 0 : 1)];
    private static readonly string[][] _pulseFrames =
    [
        [ // light theme
            "[blue]⠋[/]", "[blue]⠙[/]", "[blue]⠹[/]",
            "[blue]⠸[/]", "[blue]⠼[/]", "[blue]⠴[/]",
            "[blue]⠦[/]", "[blue]⠧[/]", "[blue]⠇[/]",
            "[blue]⠏[/]",
        ],
        [ // dark theme
            "[deepskyblue1]⠋[/]", "[deepskyblue1]⠙[/]", "[deepskyblue1]⠹[/]",
            "[deepskyblue1]⠸[/]", "[deepskyblue1]⠼[/]", "[deepskyblue1]⠴[/]",
            "[deepskyblue1]⠦[/]", "[deepskyblue1]⠧[/]", "[deepskyblue1]⠇[/]",
            "[deepskyblue1]⠏[/]",
        ],
    ];

#pragma warning disable IDE0051 // used by MessagePanelRenderer via ChatRenderer.MdToPanelContent
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
#pragma warning restore IDE0051
}
