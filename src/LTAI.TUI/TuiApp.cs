using System.Text.Json;
using Spectre.Console;
using LTAI.Agent;
using LTAI.Agent.DevUI;
using LTAI.Agent.Memory;
using LTAI.Agent.Tasks;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Mm;
using Microsoft.Extensions.Options;
using LTAI.TUI.DevUI;
using LTAI.TUI.Services;

namespace LTAI.TUI;

public enum TuiView { Dashboard, Chat, LLMConfig, TextPad, Skills, Sessions, Jobs, MemoryBrowser, Workflows, GraphBrowser }

public sealed class TuiApp
{
    private readonly ChatAgent _chat;
    private readonly LLMConfigPanel _llmConfig;
    private readonly IOptions<LTAIOptions> _config;
    private readonly string _projectRoot;
    private readonly ChatLayout _chatLayout;
    private readonly TextPadView _textPadView;
    private readonly SkillsPanelView _skillsPanelView;
    private readonly DevUI.DashboardContext _dashCtx;
    private readonly QuestionService _questionService;
    private readonly SessionsPanelView _sessionsView;
    private readonly JobsPanelView _jobsView;
    private readonly MemoryBrowserView _memoryView;
    private readonly WorkflowVisualizerView _workflowView;
    private readonly GraphBrowserView _graphView;
    private readonly SessionManager _sessionMgr;

    private TuiView _currentView = TuiView.Chat;
    private bool _running = true;

    public TuiApp(
        ChatAgent chat,
        LLMConfigPanel llmConfig,
        IOptions<LTAIOptions> config,
        string projectRoot,
        DevUI.DashboardContext dashCtx,
        QuestionService questionService,
        Rendering.ChatRenderer renderer,
        PalaceStore? palaceStore = null,
        TaskQueue? taskQueue = null,
        YAMLWorkflowRegistry? workflowRegistry = null,
        KbGraph? kbGraph = null,
        KgStore? kgStore = null)
    {
        _chat = chat;
        _llmConfig = llmConfig;
        _config = config;
        _projectRoot = projectRoot;
        _dashCtx = dashCtx;
        _sessionMgr = new SessionManager(new LTAIOptions().Session.Path, new MmSessionSerializer());
        _textPadView = new TextPadView(projectRoot);
        _skillsPanelView = new SkillsPanelView(projectRoot);
        _chatLayout = new ChatLayout(chat, renderer, questionService, _sessionMgr, _textPadView, palaceStore);
        _questionService = questionService;
        _sessionsView = new SessionsPanelView(_sessionMgr);
        _jobsView = new JobsPanelView(taskQueue);
        _memoryView = new MemoryBrowserView(palaceStore);
        _workflowView = new WorkflowVisualizerView(workflowRegistry);
        _graphView = new GraphBrowserView(kbGraph, kgStore);
    }

    public async Task RunAsync()
    {
        // 启用鼠标 + Kitty 键盘支持（整个 TUI 生命周期）
        LTAI.TUI.Input.MouseTracker.Enable();
        try
        {
        // 配置验证
        ValidateConfig();

        // 订阅工作流热重载事件
        SubscribeWorkflowEvents();

        // 首次运行向导：无 API Key 时自动弹出
        if (!_llmConfig.HasAnyConfiguredProvider())
            _llmConfig.ShowSetupWizard();

        // 自动恢复上次会话
        TryRestoreLastSession();

        // 检查 L1/L2 是否已配置（读取 appsettings.json — 单一配置文件）
        var aiCfg = _config?.Value.AI;
        var hasL1 = aiCfg?.L1 != null && !string.IsNullOrEmpty(aiCfg.L1.Provider);
        var hasL2 = aiCfg?.L2 != null && !string.IsNullOrEmpty(aiCfg.L2.Provider);
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
            catch (Exception) { }
        });
        _ = Task.Run(async () =>
        {
            try { await FetchModelInfoAsync().ConfigureAwait(false); }
            catch (Exception) { }
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
                    AnsiConsole.Clear();
                    ShowHeader();
                    ShowDashboard();
                    AnsiConsole.Write(new Rule("[grey]—[/]") { Style = Style.Parse("grey") });
                    AnsiConsole.Prompt(new SelectionPrompt<string>()
                        .Title("[dim]按 Enter 返回聊天[/]")
                        .PageSize(3)
                        .AddChoices("返回聊天"));
                    break;
                case TuiView.TextPad:
                    AnsiConsole.Clear();
                    ShowHeader();
                    _textPadView.Render();
                    // TextPadView 设置了 PendingChatRequest → 发送到聊天
                    if (_textPadView.PendingChatRequest != null)
                    {
                        _chatLayout.EnqueueUserMessage(_textPadView.PendingChatRequest);
                        _textPadView.PendingChatRequest = null;
                    }
                    break;
                case TuiView.LLMConfig:
                    _llmConfig.ShowSetupWizard();
                    break;
                case TuiView.Skills:
                    AnsiConsole.Clear();
                    ShowHeader();
                    _skillsPanelView.Render();
                    break;
                case TuiView.Sessions:
                    AnsiConsole.Clear();
                    ShowHeader();
                    _sessionsView.Render();
                    break;
                case TuiView.Jobs:
                    AnsiConsole.Clear();
                    ShowHeader();
                    _jobsView.Render();
                    break;
                case TuiView.MemoryBrowser:
                    AnsiConsole.Clear();
                    ShowHeader();
                    _memoryView.Render();
                    break;
                case TuiView.Workflows:
                    AnsiConsole.Clear();
                    ShowHeader();
                    _workflowView.Render();
                    break;
                case TuiView.GraphBrowser:
                    AnsiConsole.Clear();
                    ShowHeader();
                    _graphView.Render();
                    break;
            }
        }
        }
        finally
        {
            LTAI.TUI.Input.MouseTracker.Disable();
        }
    }

    private void ShowHeader()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{LTAI.Core.I18n.Locale.Get("View")}:[/] {_currentView}");
        AnsiConsole.MarkupLine($"[grey]" +
            $"1: {LTAI.Core.I18n.Locale.Get("Dashboard")}  " +
            $"2: {LTAI.Core.I18n.Locale.Get("Chat")}  " +
            $"3: {LTAI.Core.I18n.Locale.Get("Config")}  " +
            $"4: {LTAI.Core.I18n.Locale.Get("FileBrowser")}  " +
            $"5: {LTAI.Core.I18n.Locale.Get("Skill")}  " +
            $"6: {LTAI.Core.I18n.Locale.Get("SessionMgr")}  " +
            $"7: {LTAI.Core.I18n.Locale.Get("JobMgr")}  " +
            $"8: {LTAI.Core.I18n.Locale.Get("MemBrowser")}  " +
            $"9: {LTAI.Core.I18n.Locale.Get("WorkflowVis")}  " +
            $"0: {LTAI.Core.I18n.Locale.Get("GraphBrowse")}  " +
            $"Q: {LTAI.Core.I18n.Locale.Get("Exit")}[/]");
    }

    private async Task FetchBalanceAsync()
    {
        var provider = _config.Value.AI.DefaultProvider ?? "";
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
        if (string.IsNullOrEmpty(providerName)) return;
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

    private void TryRestoreLastSession()
    {
        try
        {
            var sessions = _sessionMgr.ListSessions();
            if (sessions.Length == 0) return;
            var last = sessions[^1]; // most recent
            var handle = _sessionMgr.LoadSession(last.Name);
            if (handle == null) return;
            var messages = handle.Messages;
            if (messages.Count == 0) return;
            foreach (var m in messages)
            {
                var role = m.Role == Microsoft.Extensions.AI.ChatRole.User ? "user" : "assistant";
                _chatLayout.EnqueueRestoredMessage(role, m.Text ?? "");
            }
            ChatLayout.SetStartupMessage($"[dim]已自动恢复会话: {last.DisplayName}[/]");
        }
        catch
        {
            // Best-effort restore
        }
    }

    private void ValidateConfig()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(configPath))
                configPath = Path.Combine(Environment.CurrentDirectory, "appsettings.json");
            if (!File.Exists(configPath))
                NotificationService.Publish("⚠️ appsettings.json not found — using defaults");
            else
            {
                var content = File.ReadAllText(configPath);
                if (string.IsNullOrWhiteSpace(content))
                    NotificationService.Publish("⚠️ appsettings.json is empty");
            }
        }
        catch (Exception ex)
        {
            NotificationService.Publish($"⚠️ Config error: {ex.Message}");
        }

        // Check persistence directory
        var livingDir = Path.Combine(Environment.CurrentDirectory, ".livingtree");
        if (!Directory.Exists(livingDir))
        {
            try { Directory.CreateDirectory(livingDir); }
            catch { NotificationService.Publish("⚠️ Cannot create .livingtree/ directory"); }
        }
    }

    private void SubscribeWorkflowEvents()
    {
        try
        {
            if (_dashCtx?.Workflows is YAMLWorkflowRegistry registry)
            {
                var notifierField = typeof(YAMLWorkflowRegistry)
                    .GetField("_notifier", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (notifierField?.GetValue(registry) is WorkflowHotReloadNotifier notifier)
                {
                    notifier.Subscribe(new WorkflowEventRelay());
                }
            }
        }
        catch { }
    }

    private sealed class WorkflowEventRelay : IWorkflowSubscriber
    {
        public void OnReloaded(WorkflowReloadEvent evt) =>
            NotificationService.Publish($"🔄 Workflow reloaded: {evt.Name} ({evt.Type})");

        public void OnLoadFailed(WorkflowLoadFailedEvent evt) =>
            NotificationService.Publish($"❌ Workflow load failed: {evt.Name} — {evt.Reason}");
    }

    private void ShowDashboard()
    {
        DevUIDashboardView.Render(
            _dashCtx.DevUi, _dashCtx.SpanCollector, UsageTracker.Default,
            _dashCtx.Workflows, _dashCtx.Embedder, _dashCtx.EmbedCache,
            _dashCtx.RemoteCache, _dashCtx.EmbeddingClient, _dashCtx.ModelsProvider,
            _dashCtx.Aligner, _dashCtx.TaskQueue, _dashCtx.Bgjs,
            _dashCtx.WorkflowHealth, _dashCtx.Palace);
    }
}

