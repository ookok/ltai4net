using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class LLMConfigPanel
{
    public record ProviderInfo(string EnvVar, string Endpoint, string Model);
    private static readonly Dictionary<string, ProviderInfo> KnownProviders = new()
    {
        ["DeepSeek"]    = new("DEEPSEEK_API_KEY",       "https://api.deepseek.com/v1",           "deepseek-chat"),
        ["SiliconFlow"] = new("SILICONFLOW_API_KEY",    "https://api.siliconflow.cn/v1",        "deepseek-ai/DeepSeek-V2.5"),
        ["Aliyun (Qwen)"] = new("DASHSCOPE_API_KEY",    "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ["Zhipu (GLM)"] = new("ZHIPU_API_KEY",          "https://open.bigmodel.cn/api/paas/v4", "glm-4-plus"),
        ["ByteDance (Doubao)"] = new("DOUBAO_API_KEY",  "https://ark.cn-beijing.volces.com/api/v3", "ep-XXXXXX"),
        ["Tencent (Hunyuan)"] = new("HUNYUAN_API_KEY",  "https://api.hunyuan.cloud.tencent.com/v1", "hunyuan-pro"),
        ["Baidu (ERNIE)"] = new("BAIDU_API_KEY",        "https://aip.baidubce.com/rpc/2.0/ai_custom", "ernie-4.0"),
        ["iFlytek (Spark)"] = new("SPARK_API_KEY",      "https://spark-api.xf-yun.com/v3.5/chat", "spark-3.5"),
        ["Moonshot (Kimi)"] = new("MOONSHOT_API_KEY",   "https://api.moonshot.cn/v1",           "moonshot-v1-8k"),
        ["Baichuan"] = new("BAICHUAN_API_KEY",          "https://api.baichuan-ai.com/v1",       "Baichuan4"),
        ["Yi (01.AI)"] = new("YI_API_KEY",              "https://api.lingyiwanwu.com/v1",       "yi-large"),
        ["StepFun (Step)"] = new("STEP_API_KEY",        "https://api.stepfun.com/v1",           "step-2-16k"),
        ["Minimax"] = new("MINIMAX_API_KEY",             "https://api.minimax.chat/v1",          "MiniMax-Text-01"),
        ["OpenAI"]      = new("OPENAI_API_KEY",          "https://api.openai.com/v1",            "gpt-4o"),
        ["Groq"]        = new("GROQ_API_KEY",            "https://api.groq.com/openai/v1",       "llama-3.3-70b-versatile"),
        ["OpenRouter"]  = new("OPENROUTER_API_KEY",      "https://openrouter.ai/api/v1",         "deepseek/deepseek-chat"),
        ["Together AI"] = new("TOGETHER_API_KEY",        "https://api.together.xyz/v1",          "mistralai/Mixtral-8x22B-Instruct-v0.1"),
        ["Mistral"]     = new("MISTRAL_API_KEY",         "https://api.mistral.ai/v1",            "mistral-large-latest"),
        ["Perplexity"]  = new("PERPLEXITY_API_KEY",      "https://api.perplexity.ai",            "sonar-pro"),
        ["X.AI (Grok)"] = new("XAI_API_KEY",             "https://api.x.ai/v1",                  "grok-2-1212"),
        ["Cohere"]      = new("COHERE_API_KEY",          "https://api.cohere.ai/v1",             "command-r-plus"),
        ["Fireworks AI"] = new("FIREWORKS_API_KEY",      "https://api.fireworks.ai/inference/v1","accounts/fireworks/models/llama-v3p3-70b-instruct"),
        ["Ollama"]      = new("",                        "http://localhost:11434/v1",            "llama3.2"),
        ["LMStudio"]    = new("",                        "http://localhost:1234/v1",             "local-model"),
        ["vLLM"]        = new("",                        "http://localhost:8000/v1",             "meta-llama/Llama-3.2-3B-Instruct"),
    };

    private readonly IOptions<LTAIOptions>? _options;
    private readonly MultiProviderChatClient? _router;
    private readonly IHttpClientFactory? _httpFactory;
    private string _provider;
    private string _l1Model;
    private string _l2Model;
    private float _temperature = 0.3f;
    private int _maxTokens = 4096;

    public string Provider => _provider;
    public ProviderInfo? CurrentProvider => KnownProviders.GetValueOrDefault(_provider);
    public string L1Model => _l1Model;
    public string L2Model => _l2Model;
    public float Temperature => _temperature;
    public int MaxTokens => _maxTokens;

    public LLMConfigPanel(IOptions<LTAIOptions>? options = null,
        MultiProviderChatClient? router = null,
        IHttpClientFactory? httpFactory = null)
    {
        _options = options;
        _router = router;
        _httpFactory = httpFactory;
        _provider = DetectActiveProvider();
        _l1Model = options?.Value.AI.GetLayerConfig("fast").Model ?? "deepseek-v4-flash";
        _l2Model = options?.Value.AI.GetLayerConfig("deep").Model ?? "deepseek-v4-pro";
    }

    private static string DetectActiveProvider()
    {
        foreach (var (name, info) in KnownProviders)
            if (!string.IsNullOrEmpty(info.EnvVar) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(info.EnvVar)))
                return name;
        return "DeepSeek";
    }

    /// <summary>Returns true if any provider has a valid API key set.</summary>
    public bool HasAnyConfiguredProvider()
    {
        return KnownProviders.Any(kv =>
            !string.IsNullOrEmpty(kv.Value.EnvVar) &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(kv.Value.EnvVar)));
    }

    /// <summary>Show an interactive first-run prompt when no providers are configured.</summary>
    public void ShowSetupWizard()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Welcome!").Color(Color.Green));
        AnsiConsole.MarkupLine("[yellow]No API keys detected. Let's set one up to get started.[/]\n");

        var options = KnownProviders
            .Where(kv => !string.IsNullOrEmpty(kv.Value.EnvVar))
            .Select(kv => kv.Key)
            .Prepend("[yellow]Enter a custom API key manually[/]")
            .ToList();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Choose a provider to configure:")
                .PageSize(15)
                .MoreChoicesText("[grey](scroll down for more)[/]")
                .AddChoices(options));

        if (choice == "[yellow]Enter a custom API key manually[/]")
        {
            var envVar = AnsiConsole.Ask<string>("[yellow]Environment variable name:[/]");
            var key = AnsiConsole.Prompt(new TextPrompt<string>($"[yellow]API Key value:[/]").Secret());
            if (!string.IsNullOrEmpty(envVar) && !string.IsNullOrEmpty(key))
            {
                Environment.SetEnvironmentVariable(envVar, key);
                var providerName = envVar.Replace("_API_KEY", "").Replace("_KEY", "");
                RegisterProviderWithRouter(providerName, envVar, key);
                _provider = providerName;
                AnsiConsole.MarkupLine($"[green]✅ Set {envVar}[/]");
            }
        }
        else if (KnownProviders.TryGetValue(choice, out var info))
        {
            var key = AnsiConsole.Prompt(new TextPrompt<string>($"[yellow]Enter {choice} API Key:[/]").Secret());
            if (!string.IsNullOrEmpty(key))
            {
                Environment.SetEnvironmentVariable(info.EnvVar, key);
                RegisterProviderWithRouter(choice, info.EnvVar, key);
                _provider = choice;
                AnsiConsole.MarkupLine($"[green]✅ {choice} configured[/]");
            }
        }

        AnsiConsole.MarkupLine("\n[grey]Press any key to continue...[/]");
        System.Console.ReadKey(true);
    }

    /// <summary>Register a new provider with the runtime router so it's immediately usable.</summary>
    private void RegisterProviderWithRouter(string name, string envVar, string apiKey)
    {
        if (_router == null || !KnownProviders.TryGetValue(name, out var info)) return;

        try
        {
            var http = _httpFactory?.CreateClient() ?? new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var client = new OpenAiHttpClient(http, info.Endpoint, info.Model, apiKey);
            _router.Register(name, client);
            _router.ActiveProvider = name;
            AnsiConsole.MarkupLine($"[green]✓ {name} registered and ready[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to register {name}: {ex.Message}[/]");
        }
    }

    public void Render()
    {
        AnsiConsole.Write(new Rule("[bold cyan]LLM Configuration[/]").RuleStyle(Style.Plain));
        AnsiConsole.MarkupLine($"[bold]Provider:[/] [cyan]{_provider}[/]");
        AnsiConsole.MarkupLine($"[bold]Mode:[/] {_options?.Value.AI.Mode ?? "balanced"}");
        AnsiConsole.WriteLine();

        var provTable = new Table().Border(TableBorder.Rounded);
        provTable.AddColumn("Provider");
        provTable.AddColumn("API Key");
        provTable.AddColumn("Endpoint");
        foreach (var (name, pInfo) in KnownProviders)
        {
            if (string.IsNullOrEmpty(pInfo.EnvVar))
                provTable.AddRow(name, "[dim](local)[/]", $"[dim]{pInfo.Endpoint}[/]");
            else
            {
                var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(pInfo.EnvVar));
                var registered = _router?.RegisteredProviders.Contains(name) == true;
                var status = hasKey
                    ? (registered ? "[green]✓[/]" : "[yellow]✓ (restart needed)[/]")
                    : "[dim]not set[/]";
                provTable.AddRow(
                    name == _provider ? $"[bold]{name}[/]" : name,
                    status,
                    $"[dim]{pInfo.Endpoint}[/]");
            }
        }
        AnsiConsole.Write(provTable);
        AnsiConsole.WriteLine();

        var prov = CurrentProvider;
        var hasApiKey = prov == null || string.IsNullOrEmpty(prov.EnvVar) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(prov.EnvVar));
        var isRegistered = prov == null || _router?.RegisteredProviders.Contains(_provider) == true;

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Layer"); table.AddColumn("Model"); table.AddColumn("Temperature"); table.AddColumn("Max Tokens"); table.AddColumn("Status");
        table.AddRow("L1 (Fast)", _l1Model, $"{_temperature:F1}", $"{_maxTokens}",
            !hasApiKey ? "[red]No Key[/]" : (!isRegistered ? "[yellow]Key set, restart to activate[/]" : "[green]Ready[/]"));
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
        var prompt = new SelectionPrompt<string>().Title("[yellow]Select provider:[/]").PageSize(10);
        foreach (var k in KnownProviders.Keys) prompt.AddChoice(k);
        var choice = AnsiConsole.Prompt(prompt);
        _provider = choice;

        var info = CurrentProvider;
        if (info != null) _l1Model = info.Model;

        // Update runtime provider in the router
        if (_router != null) _router.ActiveProvider = _provider;

        if (!string.IsNullOrEmpty(info?.EnvVar) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(info.EnvVar)))
            AnsiConsole.MarkupLine($"[yellow]⚠ No API key for {_provider}. Press K to set it.[/]");
        AnsiConsole.MarkupLine($"[green]✓ Switched to {_provider}[/]");
    }

    private void TrySetApiKey()
    {
        var info = CurrentProvider;
        if (info == null || string.IsNullOrEmpty(info.EnvVar))
        {
            AnsiConsole.MarkupLine($"[yellow]{_provider} is a local provider — no API key needed.[/]");
            return;
        }

        var key = AnsiConsole.Prompt(new TextPrompt<string>($"[yellow]Enter {_provider} API Key ({info.EnvVar}):[/]").Secret());
        if (string.IsNullOrWhiteSpace(key)) return;

        // Set process-level env var so current session sees it
        Environment.SetEnvironmentVariable(info.EnvVar, key);
        // Also save to user profile for persistence across restarts
        try { Environment.SetEnvironmentVariable(info.EnvVar, key, EnvironmentVariableTarget.User); } catch { /* best effort */ }

        // Register with the router immediately (no restart needed)
        RegisterProviderWithRouter(_provider, info.EnvVar, key);

        AnsiConsole.MarkupLine($"[green]✓ {info.EnvVar} set and registered[/]");
    }

    private void AdjustTemperature()
    {
        _temperature = AnsiConsole.Prompt(
            new TextPrompt<float>("[yellow]Temperature (0.0 - 2.0):[/]").DefaultValue(_temperature).Validate(v => v >= 0 && v <= 2));
    }

    private void AdjustMaxTokens()
    {
        _maxTokens = AnsiConsole.Prompt(
            new TextPrompt<int>("[yellow]Max Tokens:[/]").DefaultValue(_maxTokens).Validate(v => v >= 100 && v <= 128000));
    }
}
