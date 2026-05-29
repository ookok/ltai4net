using Spectre.Console;
using LTAI.Agent.MAF;
using LTAI.AI.Interfaces;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.Tools.CodeEngine;
using LTAI.Agent.Skills;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public enum TuiView { Dashboard, Chat, Code, LLMConfig }
public enum TuiTheme { Dark, Light, HighContrast }

public sealed class TuiApp
{
    private readonly ILivingTreeSystem _lts;
    private readonly MultiLangCodeAnalyzer? _analyzer;
    private readonly LLMConfigPanel _llmConfig;
    private readonly IOptions<LTAIOptions>? _configOptions;
    private readonly AgenticLoop? _agenticLoop;
    private readonly SkillRegistry? _skillRegistry;
    private readonly ChatLayout _chat;

    private TuiView _currentView = TuiView.Dashboard;
    private TuiTheme _theme = TuiTheme.Dark;
    private readonly string _projectRoot;
    private bool _running = true;
    private static readonly string[] QuickActions = { "1: Dashboard", "2: Chat", "3: Code", "4: Config", "Q: Quit" };

    public TuiApp(
        ILivingTreeSystem lts,
        MultiLangCodeAnalyzer? analyzer,
        LLMConfigPanel llmConfig,
        IOptions<LTAIOptions>? configOptions,
        AgenticLoop? agenticLoop,
        SkillRegistry? skillRegistry,
        string projectRoot)
    {
        _lts = lts;
        _analyzer = analyzer;
        _llmConfig = llmConfig;
        _configOptions = configOptions;
        _agenticLoop = agenticLoop;
        _skillRegistry = skillRegistry;
        _projectRoot = projectRoot;
        _chat = new ChatLayout(lts, null);
    }

    public async Task RunAsync()
    {
        while (_running)
        {
            AnsiConsole.Clear();
            RenderHeader();

            switch (_currentView)
            {
                case TuiView.Dashboard: RenderDashboard(); break;
                case TuiView.Chat: _chat.Render(); break;
                case TuiView.Code: RenderCodeView(); break;
                case TuiView.LLMConfig: _llmConfig.Render(); break;
            }

            RenderFooter();
            await HandleInputAsync();
        }
    }

    private void RenderHeader()
    {
        var table = new Table().Border(TableBorder.Rounded).Width(80);
        table.AddColumn(new TableColumn("[bold yellow]LTAI[/]").Centered());
        table.AddRow(_currentView switch
        {
            TuiView.Dashboard => "[bold]Dashboard[/]",
            TuiView.Chat => "[bold]Chat[/]",
            TuiView.Code => "[bold]Code[/]",
            TuiView.LLMConfig => "[bold]LLM Config[/]",
            _ => ""
        });
        AnsiConsole.Write(table);
    }

    private void RenderDashboard()
    {
        var grid = new Grid();
        grid.AddColumn();

        grid.AddRow(new Markup($"[bold]System:[/] LTAI"));

        AnsiConsole.Write(grid);
    }

    private void RenderCodeView()
    {
        AnsiConsole.MarkupLine("[bold]Code Analysis[/]");
        AnsiConsole.MarkupLine("[dim]Enter file path or 'back' to return:[/]");
    }

    private void RenderFooter()
    {
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine(string.Join("  |  ", QuickActions));
        AnsiConsole.MarkupLine("[dim]Press number key to switch views[/]");
    }

    private async Task HandleInputAsync()
    {
        var key = Console.ReadKey(true);

        // View switching always works
        switch (key.Key)
        {
            case ConsoleKey.D1: _currentView = TuiView.Dashboard; return;
            case ConsoleKey.D2: _currentView = TuiView.Chat; return;
            case ConsoleKey.D3: _currentView = TuiView.Code; return;
            case ConsoleKey.D4: _currentView = TuiView.LLMConfig; return;
            case ConsoleKey.Q: _running = false; return;
        }

        // Per-view input handling
        switch (_currentView)
        {
            case TuiView.Chat when key.Key == ConsoleKey.Enter:
            {
                var input = AnsiConsole.Prompt(
                    new TextPrompt<string>("[cyan]You:[/]").AllowEmpty());
                if (string.IsNullOrWhiteSpace(input)) return;
                AnsiConsole.Clear();
                var response = await _chat.ChatAsync(input);
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(response)}[/]");
                AnsiConsole.MarkupLine("");
                AnsiConsole.MarkupLine("[dim]Press Enter to continue or 1-4 to switch view[/]");
                Console.ReadKey(true);
                break;
            }
            case TuiView.LLMConfig:
            {
                _llmConfig.HandleKey(key);
                break;
            }
            case TuiView.Code when key.Key == ConsoleKey.Enter:
            {
                var path = AnsiConsole.Ask<string>("[yellow]File path:[/]");
                if (path.ToLowerInvariant() is "back" or "q" or "exit")
                    { _currentView = TuiView.Dashboard; return; }
                try
                {
                    var content = File.ReadAllText(path);
                    AnsiConsole.Clear();
                    AnsiConsole.Write(new Panel(new Markup($"[dim]{Markup.Escape(content.Length > 2000 ? content[..2000] + "..." : content)}[/]"))
                        .Header($"[bold]{Markup.Escape(path)}[/]").RoundedBorder().BorderColor(Color.Blue).Padding(1, 1));
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
                }
                AnsiConsole.MarkupLine("[dim]Press any key to continue[/]");
                Console.ReadKey(true);
                break;
            }
        }
    }
}
