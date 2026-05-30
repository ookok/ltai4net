using Spectre.Console;
using LTAI.Agent;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public enum TuiView { Dashboard, Chat, LLMConfig, TextPad, Skills }

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
        SkillsPanelView.Initialize(_projectRoot);
        AnsiConsole.Write(new FigletText("LTAI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — 轻量版[/]");

        // 首次运行向导：无 API Key 时自动弹出
        if (!_llmConfig.HasAnyConfiguredProvider())
        {
            _llmConfig.ShowSetupWizard();
        }

        // 异步获取余额（不阻塞）
        _ = FetchBalanceAsync();

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
                case TuiView.TextPad:
                    TextPadView.Render(_projectRoot);
                    break;
                case TuiView.Skills:
                    SkillsPanelView.Render();
                    break;
            }
            await HandleInputAsync();
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
        var apiKey = LTAI.Core.Configuration.SecretManager.Get("SILICONFLOW_API_KEY")
                  ?? LTAI.Core.Configuration.SecretManager.Get("OPENROUTER_API_KEY");
        if (apiKey != null)
            await UsageTracker.FetchBalanceAsync(_config.Value.AI.DefaultProvider, apiKey);
    }

    private void ShowDashboard()
    {
        var table = new Table();
        table.AddColumn("指标");
        table.AddColumn("值");
        table.AddRow("引擎", "LTAI (MS Agent Framework 1.8.0)");
        table.AddRow("聊天", _chat != null ? "就绪" : "N/A");
        table.AddRow("模型", _config.Value.AI.Model);
        table.AddRow("提供商", _config.Value.AI.DefaultProvider);
        table.AddRow("项目目录", _projectRoot);
        AnsiConsole.Write(table);

        // 用量统计
        AnsiConsole.MarkupLine("\n[bold]会话用量[/]");
        var usage = new Table();
        usage.AddColumn("指标"); usage.AddColumn("值");
        usage.AddRow("当前模型", UsageTracker.ActiveModel);
        usage.AddRow("输入 Token", UsageTracker.PromptTokens.ToString("N0"));
        usage.AddRow("输出 Token", UsageTracker.CompletionTokens.ToString("N0"));
        usage.AddRow("总 Token", UsageTracker.TotalTokens.ToString("N0"));
        usage.AddRow("请求次数", UsageTracker.Requests.ToString("N0"));
        var cacheRate = UsageTracker.CacheHits + UsageTracker.CacheMisses > 0
            ? UsageTracker.CacheHitRate : 0;
        usage.AddRow("缓存命中", $"{cacheRate:F1}% ({UsageTracker.CacheHits}/{UsageTracker.CacheHits + UsageTracker.CacheMisses})");
        usage.AddRow("预估费用", UsageTracker.CostDisplay);
        usage.AddRow("运行时长", UsageTracker.Uptime.ToString(@"hh\:mm\:ss"));
        usage.AddRow("账户余额", UsageTracker.BalanceDisplay);
        AnsiConsole.Write(usage);

        // 上下文容量（双层 BarChart 模拟 BreakdownBar）
        var pct = UsageTracker.ContextRatio(_config.Value.AI.MaxTokens);
        var ctxInfo = UsageTracker.ContextText(_config.Value.AI.MaxTokens);
        AnsiConsole.MarkupLine($"[bold]上下文容量:[/] {ctxInfo}");
        var ctxBar = new BarChart()
            .Width(50)
            .HideValues()
            .AddItem("已用", pct * 100, Color.Yellow)
            .AddItem("剩余", (1 - pct) * 100, Color.Grey35);
        AnsiConsole.Write(ctxBar);

        // Token 用量 BarChart
        var p = Math.Max(UsageTracker.PromptTokens, 1) / 1000.0;
        var c = Math.Max(UsageTracker.CompletionTokens, 1) / 1000.0;
        var chart = new BarChart()
            .Width(40)
            .Label("Token 用量 (K)")
            .CenterLabel()
            .AddItem("输入", p, Color.Blue)
            .AddItem("输出", c, Color.Green);
        AnsiConsole.Write(chart);

        // 缓存命中率 BarChart
        var totalCalls = UsageTracker.CacheHits + UsageTracker.CacheMisses;
        var hitPct = totalCalls > 0 ? UsageTracker.CacheHitRate / 100.0 : 0;
        var missPct = 1.0 - hitPct;
        var cacheChart = new BarChart()
            .Width(40)
            .Label("缓存命中率")
            .CenterLabel()
            .AddItem("命中", hitPct * 100, Color.Green)
            .AddItem("未命中", missPct * 100, Color.Red);
        AnsiConsole.Write(cacheChart);
    }

    private async Task HandleInputAsync()
    {
        var key = Console.ReadKey(true);

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
            case '4': _currentView = TuiView.TextPad; break;
            case '5': _currentView = TuiView.Skills; break;
            case 'q': case 'Q': _running = false; break;
        }
    }
}
