using System.Text.Json;
using Spectre.Console;
using LTAI.Agent;
using LTAI.Agent.DevUI;
using LTAI.Agent.Tools;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;
using LTAI.TUI.DevUI;

namespace LTAI.TUI;

public enum TuiView { Dashboard, Chat, LLMConfig, TextPad, Skills }

public sealed class TuiApp
{
    private readonly ChatAgent _chat;
    private readonly LLMConfigPanel _llmConfig;
    private readonly IOptions<LTAIOptions> _config;
    private readonly string _projectRoot;
    private readonly ChatLayout _chatLayout;
    private readonly LTAIDevUIService _devUi;
    private readonly DevUISpanCollector _spanCollector;
    private readonly LTAI.Agent.Workflows.YAMLWorkflowRegistry? _workflows;
    private readonly LTAI.AI.LocalEmbedder? _embedder;
    private readonly LTAI.AI.ToolEmbeddingCache? _embedCache;
    private readonly LTAI.AI.RemoteEmbeddingCache? _remoteCache;
    private readonly LTAI.AI.EmbeddingClient? _embeddingClient;
    private readonly LTAI.AI.ModelMetadataProvider? _modelsProvider;
    private readonly QuestionService _questionService;

    private TuiView _currentView = TuiView.Chat;
    private bool _running = true;

    public TuiApp(
        ChatAgent chat,
        LLMConfigPanel llmConfig,
        IOptions<LTAIOptions> config,
        string projectRoot,
        LTAIDevUIService devUi,
        DevUISpanCollector spanCollector,
        QuestionService questionService,
        LTAI.Agent.Workflows.YAMLWorkflowRegistry? workflows = null,
        LTAI.AI.LocalEmbedder? embedder = null,
        LTAI.AI.ToolEmbeddingCache? embedCache = null,
        LTAI.AI.RemoteEmbeddingCache? remoteCache = null,
        LTAI.AI.EmbeddingClient? embeddingClient = null,
        LTAI.AI.ModelMetadataProvider? modelsProvider = null)
    {
        _chat = chat;
        _llmConfig = llmConfig;
        _config = config;
        _projectRoot = projectRoot;
        _chatLayout = new ChatLayout(chat, questionService);
        _devUi = devUi;
        _spanCollector = spanCollector;
        _questionService = questionService;
        _workflows = workflows;
        _embedder = embedder;
        _embedCache = embedCache;
        _remoteCache = remoteCache;
        _embeddingClient = embeddingClient;
        _modelsProvider = modelsProvider;
    }

    public async Task RunAsync()
    {
        SkillsPanelView.Initialize(_projectRoot);

        // 首次运行向导：无 API Key 时自动弹出
        if (!_llmConfig.HasAnyConfiguredProvider())
            _llmConfig.ShowSetupWizard();

        // 检查 L1/L2 是否已配置，未配置时在对话区提示
        var layersPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "layers.json");
        var hasL1 = false; var hasL2 = false;
        if (File.Exists(layersPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(layersPath));
                hasL1 = doc.RootElement.TryGetProperty("l1", out _);
                hasL2 = doc.RootElement.TryGetProperty("l2", out _);
            }
            catch { }
        }
        if (!hasL1 || !hasL2)
        {
            var missing = new List<string>();
            if (!hasL1) missing.Add("L1");
            if (!hasL2) missing.Add("L2");
            ChatLayout.SetStartupMessage(
                $"{string.Join("+", missing)} 未配置，请在对话区输入 [bold]/model {(!hasL2 ? "l2" : "l1")}[/] 选择 provider 和模型");
        }

        // 异步获取余额 + 模型信息（不阻塞）
        _ = Task.Run(async () =>
        {
            try { await FetchBalanceAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FetchBalanceAsync: {ex.Message}"); }
        });
        _ = Task.Run(async () =>
        {
            try { await FetchModelInfoAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FetchModelInfoAsync: {ex.Message}"); }
        });

        // 主循环：多视图导航
        while (_running)
        {
            var target = await _chatLayout.RenderAsync().ConfigureAwait(false);
            if (target == null) { _running = false; break; }
            if (target == TuiView.Chat) continue;

            // 临时切换到其他视图，完成后回到聊天
            _currentView = target.Value;
            switch (target)
            {
                case TuiView.Dashboard:
                    Console.Clear();
                    ShowHeader();
                    ShowDashboard();
                    AnsiConsole.Write(new Rule("[grey]—[/]") { Style = Style.Parse("grey") });
                    AnsiConsole.MarkupLine("[dim]按任意键返回聊天...[/]");
                    Console.ReadKey(true);
                    break;
                case TuiView.TextPad:
                    Console.Clear();
                    ShowHeader();
                    TextPadView.Render(_projectRoot);
                    break;
                case TuiView.LLMConfig:
                    _llmConfig.ShowSetupWizard();
                    break;
                case TuiView.Skills:
                    Console.Clear();
                    ShowHeader();
                    SkillsPanelView.Render();
                    break;
            }
        }
    }

    private void ShowHeader()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]视图:[/] {_currentView}");
        AnsiConsole.MarkupLine("[grey]1: 仪表盘  2: 聊天  3: 配置  4: 文件  5: 技能  Q: 退出[/]");
    }

    private async Task FetchBalanceAsync()
    {
        var provider = _config.Value.AI.DefaultProvider;
        var apiKey = provider switch
        {
            { } p when p.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
                => SecretManager.Get("DEEPSEEK_API_KEY"),
            { } p when p.Contains("siliconflow", StringComparison.OrdinalIgnoreCase)
                => SecretManager.Get("SILICONFLOW_API_KEY"),
            { } p when p.Contains("openrouter", StringComparison.OrdinalIgnoreCase)
                => SecretManager.Get("OPENROUTER_API_KEY"),
            { } p when p.Contains("zhipu", StringComparison.OrdinalIgnoreCase)
                => SecretManager.Get("ZHIPU_API_KEY"),
            { } p when p.Contains("aliyun", StringComparison.OrdinalIgnoreCase)
                  || p.Contains("dashscope", StringComparison.OrdinalIgnoreCase)
                => SecretManager.Get("DASHSCOPE_API_KEY"),
            { } p when p.Contains("moonshot", StringComparison.OrdinalIgnoreCase)
                => SecretManager.Get("MOONSHOT_API_KEY"),
            _ => null
        };
        if (apiKey != null)
            await UsageTracker.FetchBalanceAsync(provider, apiKey).ConfigureAwait(false);
    }

    private async Task FetchModelInfoAsync()
    {
        // Find the default provider's endpoint from KnownKeys
        var options = _config.Value;
        var providerName = options.AI.DefaultProvider;
        var keyInfo = KnownKeys.All.FirstOrDefault(k =>
            k.EnvVar != null && k.Endpoint != null &&
            k.Service.Contains(providerName, StringComparison.OrdinalIgnoreCase));
        if (keyInfo == null) return;

        var apiKey = SecretManager.Get(keyInfo.EnvVar!);
        if (string.IsNullOrEmpty(apiKey)) return;

        // Also try DEEPSEEK_API_KEY as fallback for standard endpoint
        apiKey ??= SecretManager.Get("DEEPSEEK_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return;

        // Try the configured endpoint first, then the default provider endpoint
        var endpoint = options.AI.Providers.GetValueOrDefault("deepseek-fast")?.Endpoint
                    ?? options.AI.Providers.GetValueOrDefault("deepseek-pro")?.Endpoint
                    ?? keyInfo.Endpoint;
        if (!string.IsNullOrEmpty(endpoint))
            await UsageTracker.RefreshModelInfoAsync(endpoint, apiKey).ConfigureAwait(false);
    }

    private void ShowDashboard()
    {
        DevUIDashboardView.Render(_devUi, _spanCollector, UsageTracker.Default, _workflows, _embedder, _embedCache, _remoteCache, _embeddingClient, _modelsProvider);
    }

}

