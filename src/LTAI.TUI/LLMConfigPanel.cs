using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;
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
        ["Anthropic"]   = new("ANTHROPIC_API_KEY",       "https://api.anthropic.com/v1",         "claude-3-5-sonnet-20241022"),
        ["Gemini"]      = new("GEMINI_API_KEY",          "https://generativelanguage.googleapis.com/v1beta", "gemini-2.0-flash"),
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

    public LLMConfigPanel(IOptions<LTAIOptions>? options = null, MultiProviderChatClient? router = null)
    {
        _options = options;
        _router = router;
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
        foreach (var (name, info) in KnownProviders)
        {
            if (string.IsNullOrEmpty(info.EnvVar))
                provTable.AddRow(name, "[dim](local)[/]", $"[dim]{info.Endpoint}[/]");
            else
            {
                var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(info.EnvVar));
                provTable.AddRow(
                    name == _provider ? $"[bold]{name}[/]" : name,
                    hasKey ? "[green]✓[/]" : "[dim]not set[/]",
                    $"[dim]{info.Endpoint}[/]");
            }
        }
        AnsiConsole.Write(provTable);
        AnsiConsole.WriteLine();

        var prov = CurrentProvider;
        var keyOk = prov == null || string.IsNullOrEmpty(prov.EnvVar) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(prov.EnvVar));

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Layer"); table.AddColumn("Model"); table.AddColumn("Temperature"); table.AddColumn("Max Tokens"); table.AddColumn("Key");
        table.AddRow("L1 (Fast)", _l1Model, $"{_temperature:F1}", $"{_maxTokens}", keyOk ? "[green]✓[/]" : "[red]No Key[/]");
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
        Environment.SetEnvironmentVariable(info.EnvVar, key, EnvironmentVariableTarget.User);
        AnsiConsole.MarkupLine($"[green]✓ {info.EnvVar} saved[/]");
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
