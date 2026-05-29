using Spectre.Console;
using LTAI.Agent;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public enum TuiView { Dashboard, Chat, LLMConfig }

public sealed class TuiApp
{
    private readonly ChatAgent _chat;
    private readonly LLMConfigPanel _llmConfig;
    private readonly IOptions<LTAIOptions> _config;
    private readonly string _projectRoot;
    private readonly ChatLayout _chatLayout;

    private TuiView _currentView = TuiView.Dashboard;
    private bool _running = true;

    public TuiApp(ChatAgent chat, LLMConfigPanel llmConfig, IOptions<LTAIOptions> config, string projectRoot)
    {
        _chat = chat;
        _llmConfig = llmConfig;
        _config = config;
        _projectRoot = projectRoot;
        _chatLayout = new ChatLayout(chat);
    }

    public async Task RunAsync()
    {
        AnsiConsole.Write(new FigletText("LTAI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — Simplified[/]");

        // First-run setup: if no API keys configured, show interactive wizard
        if (!_llmConfig.HasAnyConfiguredProvider())
        {
            _llmConfig.ShowSetupWizard();
        }

        while (_running)
        {
            ShowHeader();
            switch (_currentView)
            {
                case TuiView.Dashboard:
                    ShowDashboard();
                    break;
                case TuiView.Chat:
                    await _chatLayout.RenderAsync();
                    break;
                case TuiView.LLMConfig:
                    _llmConfig.Render();
                    break;
            }
            await HandleInputAsync();
        }
    }

    private void ShowHeader()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]View:[/] {_currentView}");
        AnsiConsole.MarkupLine("[grey]1: Dashboard  2: Chat  3: Config  Q: Quit[/]");
    }

    private void ShowDashboard()
    {
        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Agent", "LTAI (MS Agent Framework 1.8.0)");
        table.AddRow("Chat", _chat != null ? "Ready" : "N/A");
        table.AddRow("Model", _config.Value.AI.Model);
        table.AddRow("Provider", _config.Value.AI.DefaultProvider);
        AnsiConsole.Write(table);
    }

    private async Task HandleInputAsync()
    {
        var key = Console.ReadKey(true);

        // Route to LLM config panel when in config view
        if (_currentView == TuiView.LLMConfig)
        {
            _llmConfig.HandleKey(key);
            return;
        }

        switch (key.KeyChar)
        {
            case '1': _currentView = TuiView.Dashboard; break;
            case '2': _currentView = TuiView.Chat; break;
            case '3': _currentView = TuiView.LLMConfig; break;
            case 'q': case 'Q': _running = false; break;
        }
    }
}
