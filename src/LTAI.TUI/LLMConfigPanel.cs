using Spectre.Console.Rendering;
using Spectre.Console;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public sealed class LLMConfigPanel
{
    private readonly IOptions<LTAIOptions>? _options;
    private int _activeLayer; // 0=L0, 1=L1, 2=L2
    private string _l0Model;
    private string _l1Model;
    private string _l2Model;
    private float _temperature = 0.3f;
    private int _maxTokens = 4096;

    public string SelectedModel => _activeLayer switch
    {
        0 => _l0Model,
        1 => _l1Model,
        _ => _l2Model
    };

    public string ActiveLayerName => _activeLayer switch { 0 => "L0", 1 => "L1", _ => "L2" };

    public string L0Model => _l0Model;
    public string L1Model => _l1Model;
    public string L2Model => _l2Model;
    public int ActiveLayer => _activeLayer;
    public float Temperature => _temperature;
    public int MaxTokens => _maxTokens;

    public LLMConfigPanel(IOptions<LTAIOptions>? options = null)
    {
        _options = options;
        _l0Model = options?.Value.AI.L0.Model ?? "text-embedding-v1";
        _l1Model = options?.Value.AI.L1.Model ?? "deepseek-chat";
        _l2Model = options?.Value.AI.L2.Model ?? "deepseek-v4-pro";
        _activeLayer = 2;
    }

    public IRenderable Render()
    {
        var providers = _options?.Value.AI.Providers ?? new Dictionary<string, ProviderConfig>();
        if (providers.Count == 0)
            return new Markup("[grey]No providers configured.[/]");

        var sb = new System.Text.StringBuilder();

        var l0Icon = _activeLayer == 0 ? "[green]▶[/]" : "[grey] [/]";
        var l1Icon = _activeLayer == 1 ? "[green]▶[/]" : "[grey] [/]";
        var l2Icon = _activeLayer == 2 ? "[green]▶[/]" : "[grey] [/]";

        var hasL0Key = HasApiKey(GetLayerProvider(0));
        var hasL1Key = HasApiKey(GetLayerProvider(1));
        var hasL2Key = HasApiKey(GetLayerProvider(2));

        var l0KeyStatus = !string.IsNullOrEmpty(GetLayerProvider(0)) ? (hasL0Key ? "[green]🔑[/]" : "[red]🔒[/]") : "[grey]—[/]";
        var l1KeyStatus = !string.IsNullOrEmpty(GetLayerProvider(1)) ? (hasL1Key ? "[green]🔑[/]" : "[red]🔒[/]") : "[grey]—[/]";
        var l2KeyStatus = !string.IsNullOrEmpty(GetLayerProvider(2)) ? (hasL2Key ? "[green]🔑[/]" : "[red]🔒[/]") : "[grey]—[/]";

        sb.AppendLine($"Layers:  {l0Icon} [cyan]L0[/] {l0KeyStatus} [white]{_l0Model}[/]");
        sb.AppendLine($"         {l1Icon} [cyan]L1[/] {l1KeyStatus} [white]{_l1Model}[/]");
        sb.AppendLine($"         {l2Icon} [cyan]L2[/] {l2KeyStatus} [white]{_l2Model}[/]");

        sb.AppendLine();
        sb.AppendLine($"[cyan]Temperature:[/] [yellow]{_temperature:F1}[/] [grey](0=precise, 1=creative)[/]");
        sb.AppendLine($"[cyan]Max Tokens:[/] [yellow]{_maxTokens}[/]");

        sb.AppendLine();
        sb.AppendLine("[grey]Keys: Tab cycle layer  ↑↓ switch model  +/- temp  ←→ tokens  K input API Key[/]");

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Header = new PanelHeader($"[yellow]LLM [{ActiveLayerName}] — {SelectedModel}[/]"),
            Border = BoxBorder.Rounded
        };
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        var providers = _options?.Value.AI.Providers;
        if (providers == null || providers.Count == 0) return;

        var currentModel = SelectedModel;
        var chatProviders = GetChatProviders();

        var idx = chatProviders.IndexOf(currentModel);
        if (idx < 0) idx = chatProviders.Count > 0 ? 0 : -1;

        switch (key.Key)
        {
            case ConsoleKey.Tab:
                _activeLayer = (_activeLayer + 1) % 3;
                break;
            case ConsoleKey.UpArrow when chatProviders.Count > 0:
                SetLayerModel(_activeLayer, chatProviders[(idx - 1 + chatProviders.Count) % chatProviders.Count]);
                break;
            case ConsoleKey.DownArrow when chatProviders.Count > 0:
                SetLayerModel(_activeLayer, chatProviders[(idx + 1) % chatProviders.Count]);
                break;
            case ConsoleKey.K:
                TryPromptApiKey();
                break;
            case ConsoleKey.OemPlus or ConsoleKey.Add:
                _temperature = Math.Min(2.0f, _temperature + 0.1f);
                break;
            case ConsoleKey.OemMinus or ConsoleKey.Subtract:
                _temperature = Math.Max(0f, _temperature - 0.1f);
                break;
            case ConsoleKey.RightArrow:
                _maxTokens = Math.Min(128000, _maxTokens * 2);
                break;
            case ConsoleKey.LeftArrow:
                _maxTokens = Math.Max(256, _maxTokens / 2);
                break;
        }
    }

    public void CycleModel()
    {
        var chatProviders = GetChatProviders();
        if (chatProviders.Count == 0) return;

        var current = SelectedModel;
        var idx = chatProviders.IndexOf(current);
        if (idx < 0) idx = chatProviders.Count - 1;
        SetLayerModel(_activeLayer, chatProviders[(idx + 1) % chatProviders.Count]);
    }

    public string? GetModelForChat()
    {
        return _activeLayer == 0 ? _l0Model :
               _activeLayer == 1 ? _l1Model : _l2Model;
    }

    public IReadOnlyList<string> GetProviders()
    {
        return _options?.Value.AI.Providers.Keys.ToList() ?? new List<string>();
    }

    private List<string> GetChatProviders()
    {
        var providers = _options?.Value.AI.Providers;
        if (providers == null || providers.Count == 0) return new();
        return providers.Keys.ToList();
    }

    private void SetLayerModel(int layer, string modelName)
    {
        switch (layer)
        {
            case 0: _l0Model = modelName; break;
            case 1: _l1Model = modelName; break;
            case 2: _l2Model = modelName; break;
        }
    }

    private string? GetLayerProvider(int layer)
    {
        return layer switch
        {
            0 => _options?.Value.AI.L0.Provider,
            1 => _options?.Value.AI.L1.Provider,
            _ => _options?.Value.AI.L2.Provider
        };
    }

    private bool HasApiKey(string? provider)
    {
        if (string.IsNullOrEmpty(provider)) return true; // local mode, no key needed
        var envKey = provider.ToUpperInvariant() switch
        {
            "DEEPSEEK" => "DEEPSEEK_API_KEY",
            "SILICONFLOW" => "SILICONFLOW_API_KEY",
            "ALIYUN" => "DASHSCOPE_API_KEY",
            "OPENAI" => "OPENAI_API_KEY",
            "ANTHROPIC" => "ANTHROPIC_API_KEY",
            _ => $"{provider.ToUpperInvariant()}_API_KEY"
        };
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envKey));
    }

    private void TryPromptApiKey()
    {
        var provider = GetLayerProvider(_activeLayer);
        if (string.IsNullOrEmpty(provider))
        {
            AnsiConsole.MarkupLine("[grey]No provider configured for this layer.[/]");
            return;
        }

        var envKey = provider.ToUpperInvariant() switch
        {
            "DEEPSEEK" => "DEEPSEEK_API_KEY",
            "SILICONFLOW" => "SILICONFLOW_API_KEY",
            "ALIYUN" => "DASHSCOPE_API_KEY",
            "OPENAI" => "OPENAI_API_KEY",
            "ANTHROPIC" => "ANTHROPIC_API_KEY",
            _ => $"{provider.ToUpperInvariant()}_API_KEY"
        };

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envKey)))
        {
            AnsiConsole.MarkupLine($"[green]✓ API Key already set ({envKey})[/]");
            return;
        }

        var key = AnsiConsole.Prompt(
            new TextPrompt<string>($"[yellow]Enter API Key for {provider} ({envKey}):[/]")
                .Secret());
        if (string.IsNullOrWhiteSpace(key)) return;

        try
        {
            Environment.SetEnvironmentVariable(envKey, key, EnvironmentVariableTarget.User);
            AnsiConsole.MarkupLine($"[green]✓ API Key saved to {envKey} (persistent)[/]");
        }
        catch
        {
            Environment.SetEnvironmentVariable(envKey, key, EnvironmentVariableTarget.Process);
            AnsiConsole.MarkupLine($"[yellow]✓ API Key set for current session only[/]");
        }
    }
}
