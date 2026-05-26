using Spectre.Console.Rendering;
using Spectre.Console;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public sealed class LLMConfigPanel
{
    private readonly IOptions<LTAIOptions>? _options;
    private string _selectedModel;
    private float _temperature = 0.3f;
    private int _maxTokens = 4096;
    public string SelectedModel => _selectedModel;
    public float Temperature => _temperature;
    public int MaxTokens => _maxTokens;

    public LLMConfigPanel(IOptions<LTAIOptions>? options = null)
    {
        _options = options;
        _selectedModel = options?.Value.AI.L2.Model ?? "deepseek-v4-pro";
    }

    public IRenderable Render()
    {
        var providers = _options?.Value.AI.Providers ?? new Dictionary<string, ProviderConfig>();
        if (providers.Count == 0)
            return new Markup("[grey]No providers configured.[/]");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[cyan]Model:[/] [bold white]{_selectedModel}[/]");
        sb.AppendLine($"[cyan]Temperature:[/] [yellow]{_temperature:F1}[/] [grey](0=precise, 1=creative)[/]");
        sb.AppendLine($"[cyan]Max Tokens:[/] [yellow]{_maxTokens}[/]");

        var budget = _options?.Value.AI.DailyBudgetUsd ?? 10m;
        sb.AppendLine($"[cyan]Daily Budget:[/] [yellow]${budget:F2}[/] [grey]USD[/]");

        sb.AppendLine();
        sb.AppendLine("[cyan]Providers:[/]");

        foreach (var (key, config) in providers)
        {
            var active = key == _selectedModel ? "[green]●[/]" : "[grey]○[/]";
            var model = config.Model ?? key;
            var costLabel = key.Contains("flash") ? "[grey]fast[/]" :
                           key.Contains("pro") ? "[yellow]pro[/]" :
                           "[green]std[/]";
            sb.AppendLine($"  {active} [white]{key}[/] {costLabel} → {model}");
        }

        sb.AppendLine();
        sb.AppendLine("[grey]Keys: ↑↓ select model, +/- temperature, [/] filter[/]");

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Header = new PanelHeader("[yellow]LLM Configuration[/]"),
            Border = BoxBorder.Rounded
        };
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        var providers = _options?.Value.AI.Providers;
        if (providers == null || providers.Count == 0) return;

        var keys = providers.Keys.ToList();
        var idx = keys.IndexOf(_selectedModel);

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (idx > 0) _selectedModel = keys[idx - 1];
                break;
            case ConsoleKey.DownArrow:
                if (idx < keys.Count - 1) _selectedModel = keys[idx + 1];
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
        var providers = _options?.Value.AI.Providers;
        if (providers == null || providers.Count == 0) return;

        var chatProviders = new List<string>();
        var l1Name = _options?.Value.AI.L1.Provider;
        var l2Name = _options?.Value.AI.L2.Provider;

        foreach (var key in providers.Keys)
        {
            if (key == l1Name || key == l2Name ||
                key.EndsWith("-flash") || key.EndsWith("-pro") ||
                key.Contains("deepseek") || key.Contains("qwen") || key.Contains("gpt"))
                chatProviders.Add(key);
        }

        if (chatProviders.Count == 0)
        {
            chatProviders.AddRange(providers.Keys);
        }

        var idx = chatProviders.IndexOf(_selectedModel);
        if (idx < 0) idx = chatProviders.Count - 1;
        _selectedModel = chatProviders[(idx + 1) % chatProviders.Count];
    }

    public IReadOnlyList<string> GetProviders()
    {
        return _options?.Value.AI.Providers.Keys.ToList() ?? new List<string>();
    }
}
