using Spectre.Console;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public sealed class LLMConfigPanel
{
    // Provider list matching CLI's env command — key short name → env var name
    private static readonly Dictionary<string, string> KnownProviders = new()
    {
        ["DeepSeek"] = "DEEPSEEK_API_KEY",
        ["OpenAI"] = "OPENAI_API_KEY",
        ["Anthropic"] = "ANTHROPIC_API_KEY",
        ["Gemini"] = "GEMINI_API_KEY",
        ["SiliconFlow"] = "SILICONFLOW_API_KEY",
        ["Aliyun (DashScope)"] = "DASHSCOPE_API_KEY",
        ["Zhipu"] = "ZHIPU_API_KEY",
        ["Tencent (Hunyuan)"] = "HUNYUAN_API_KEY",
        ["Baidu"] = "BAIDU_API_KEY",
        ["iFlytek (Spark)"] = "SPARK_API_KEY",
        ["Moonshot"] = "MOONSHOT_API_KEY",
        ["Groq"] = "GROQ_API_KEY",
        ["OpenRouter"] = "OPENROUTER_API_KEY",
        ["Ollama (local)"] = "",          // no key needed
        ["LMStudio (local)"] = "",         // no key needed
        ["vLLM (local)"] = "",             // no key needed
    };

    private readonly IOptions<LTAIOptions>? _options;
    private string _provider;
    private string _l1Model;
    private string _l2Model;
    private float _temperature = 0.3f;
    private int _maxTokens = 4096;

    public string L1Model => _l1Model;
    public string L2Model => _l2Model;
    public float Temperature => _temperature;
    public int MaxTokens => _maxTokens;

    public LLMConfigPanel(IOptions<LTAIOptions>? options = null)
    {
        _options = options;
        _provider = DetectActiveProvider();
        _l1Model = options?.Value.AI.GetLayerConfig("fast").Model ?? "deepseek-v4-flash";
        _l2Model = options?.Value.AI.GetLayerConfig("deep").Model ?? "deepseek-v4-pro";
    }

    /// <summary>Auto-detect which provider has credentials configured.</summary>
    private static string DetectActiveProvider()
    {
        foreach (var (name, envVar) in KnownProviders)
        {
            if (!string.IsNullOrEmpty(envVar) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
                return name;
        }
        // Fall back to first entry in options
        return "DeepSeek";
    }

    public void Render()
    {
        AnsiConsole.Write(new Rule("[bold cyan]LLM Configuration[/]").RuleStyle(Style.Plain));
        AnsiConsole.MarkupLine($"[bold]Provider:[/] [cyan]{_provider}[/]");
        AnsiConsole.MarkupLine($"[bold]Mode:[/] {_options?.Value.AI.Mode ?? "balanced"}");
        AnsiConsole.WriteLine();

        // Provider availability table
        var provTable = new Table().Border(TableBorder.Rounded);
        provTable.AddColumn("Provider");
        provTable.AddColumn("API Key");
        foreach (var (name, envVar) in KnownProviders)
        {
            if (string.IsNullOrEmpty(envVar))
                provTable.AddRow(name, "[dim](local)[/]");
            else
            {
                var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar));
                provTable.AddRow(
                    name == _provider ? $"[bold]{name}[/]" : name,
                    hasKey ? "[green]✓ configured[/]" : "[dim]not set[/]");
            }
        }
        AnsiConsole.Write(provTable);
        AnsiConsole.WriteLine();

        // Model config table
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Layer");
        table.AddColumn("Model");
        table.AddColumn("Temperature");
        table.AddColumn("Max Tokens");
        table.AddColumn("Key Status");

        var providerEv = KnownProviders.TryGetValue(_provider, out var ev) ? ev : "DEEPSEEK_API_KEY";
        var keyOk = string.IsNullOrEmpty(providerEv) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(providerEv));

        table.AddRow("L1 (Fast)", _l1Model, $"{_temperature:F1}", $"{_maxTokens}", keyOk ? "[green]✓[/]" : "[red]No Key[/]");
        table.AddRow("L2 (Deep)", _l2Model, $"{_temperature:F1}", $"{_maxTokens}", keyOk ? "[green]✓[/]" : "[red]No Key[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]P: switch provider | K: set API key | T: temperature | M: max tokens[/]");
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.P: SwitchProvider(); break;
            case ConsoleKey.K: TrySetApiKey(); break;
            case ConsoleKey.T: AdjustTemperature(); break;
            case ConsoleKey.M: AdjustMaxTokens(); break;
        }
    }

    private void SwitchProvider()
    {
        var prompt = new SelectionPrompt<string>()
            .Title("[yellow]Select provider:[/]")
            .PageSize(10);
        foreach (var k in KnownProviders.Keys)
            prompt.AddChoice(k);
        var choice = AnsiConsole.Prompt(prompt);
        _provider = choice;

        // Refresh model names from config if available for this provider
        var providerKey = choice.ToLower(System.Globalization.CultureInfo.InvariantCulture);
        if (_options != null && _options.Value.AI.Providers.TryGetValue(providerKey, out var cfg))
        {
            _l1Model = cfg.Model;
            _l2Model = cfg.Model; // fallback, layer-specific models override below
        }

        var envVar = KnownProviders.TryGetValue(_provider, out var ev) ? ev : null;
        if (!string.IsNullOrEmpty(envVar))
        {
            var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar));
            if (!hasKey)
                AnsiConsole.MarkupLine($"[yellow]⚠ No API key found for {_provider}. Press K to set it.[/]");
        }
        AnsiConsole.MarkupLine($"[green]✓ Switched to {_provider}[/]");
    }

    private void TrySetApiKey()
    {
        var providerName = _provider;
        var envVar = KnownProviders.TryGetValue(providerName, out var ev) ? ev : "DEEPSEEK_API_KEY";

        if (string.IsNullOrEmpty(envVar))
        {
            AnsiConsole.MarkupLine("[yellow]{providerName} is a local provider — no API key needed.[/]");
            return;
        }

        var key = AnsiConsole.Prompt(
            new TextPrompt<string>($"[yellow]Enter {providerName} API Key (will be saved as {envVar}):[/]").Secret());
        if (string.IsNullOrWhiteSpace(key)) return;
        Environment.SetEnvironmentVariable(envVar, key, EnvironmentVariableTarget.User);
        AnsiConsole.MarkupLine($"[green]✓ {envVar} saved[/]");
    }

    private void AdjustTemperature()
    {
        _temperature = AnsiConsole.Prompt(
            new TextPrompt<float>("[yellow]Temperature (0.0 - 2.0):[/]")
                .DefaultValue(_temperature)
                .Validate(v => v >= 0 && v <= 2));
    }

    private void AdjustMaxTokens()
    {
        _maxTokens = AnsiConsole.Prompt(
            new TextPrompt<int>("[yellow]Max Tokens:[/]")
                .DefaultValue(_maxTokens)
                .Validate(v => v >= 100 && v <= 128000));
    }

    private static bool HasApiKey(string envVar)
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar));
    }
}
