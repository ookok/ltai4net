using System.Globalization;

namespace LTAI.Core.I18n;

/// <summary>
/// Centralized locale detection and string lookup for the LTAI project.
/// Detects OS language at startup via <see cref="CultureInfo.CurrentUICulture"/>
/// and provides culture-aware string access without requiring .resx files.
///
/// <b>Usage:</b>
/// <code>
///   var msg = Locale.Get("ChatView_NewSessionCreated");
///   // Returns Chinese or English string based on OS UI language
/// </code>
///
/// <b>Design decision (D100):</b> No .resx files. Strings are defined as static
/// dictionaries keyed by invariant ID. This avoids satellite assembly complexity
/// and keeps i18n zero-dependency. The dictionary approach also allows runtime
/// switching (TUI /lang command) and JSON export for translators.
///
/// Supported cultures: zh-CN (default), en-US.
/// Falls back to zh-CN when CurrentUICulture is not matched.
/// </summary>
public static class Locale
{
    private static readonly Dictionary<string, string> _zh = new()
    {
        // ── General ──
        ["AppName"] = "LTAI 智能助手",
        ["Loading"] = "加载中…",
        ["Ready"] = "就绪",
        ["Error"] = "错误",
        ["Success"] = "成功",
        ["Cancel"] = "取消",
        ["Confirm"] = "确认",
        ["Save"] = "保存",
        ["Delete"] = "删除",
        ["Close"] = "关闭",
        ["Retry"] = "重试",
        ["Back"] = "返回",
        ["Next"] = "下一步",
        ["Reset"] = "重置",
        ["Unavailable"] = "不可用",
        ["Unknown"] = "未知",
        ["NotFound"] = "未找到",

        // ── TUI ──
        ["NewSessionCreated"] = "新会话已创建",
        ["SessionCleared"] = "会话已清空",
        ["Exiting"] = "正在退出…",
        ["View"] = "视图",
        ["HelpTitle"] = "帮助信息",
        ["StatusTitle"] = "当前状态",
        ["ModelTitle"] = "模型管理",
        ["CostTitle"] = "费用统计",
        ["MemoryTitle"] = "记忆管理",
        ["SnippetTitle"] = "常用语",
        ["WorkflowTitle"] = "工作流",
        ["JobsTitle"] = "后台作业",
        ["PipelineTitle"] = "管道编排",
        ["NoSnippets"] = "暂无常用语",
        ["SnippetSaved"] = "已保存常用语",
        ["SnippetDeleted"] = "已删除常用语",
        ["SnippetNotFound"] = "找不到常用语",
        ["SnippetUsage"] = "用法: /snippet save <key> <text>",
        ["JobRunning"] = "运行中",
        ["JobCompleted"] = "完成",
        ["JobCancelled"] = "取消",
        ["JobFailed"] = "失败",
        ["NoJobs"] = "暂无后台作业",
        ["JobsCount"] = "共 {0} 个作业 ({1} 运行中)",
        ["WorkflowCount"] = "{0} 个 workflow",
        ["LastReload"] = "上次重载",
        ["NoWorkflows"] = "无 workflow",
        ["You"] = "你",
        ["AI"] = "AI",
        ["Tool"] = "工具",
        ["Error"] = "错误",
        ["System"] = "系统",
        ["MemList"] = "记忆列表",
        ["MemSearch"] = "搜索记忆",
        ["MemDelete"] = "删除记忆",
        ["MemStats"] = "记忆统计",
        ["NoMemory"] = "暂无记忆",
        ["FileBrowser"] = "文件浏览器",
        ["FullScreenEdit"] = "全屏编辑",
        ["Dashboard"] = "仪表盘",
        ["Chat"] = "聊天",
        ["Config"] = "配置",
        ["Skill"] = "技能",
        ["SessionMgr"] = "会话管理",
        ["JobMgr"] = "后台作业",
        ["MemBrowser"] = "记忆浏览",
        ["WorkflowVis"] = "工作流可视化",
        ["GraphBrowse"] = "知识图谱",
        ["Exit"] = "退出",

        // ── Desktop ──
        ["InitFailed"] = "服务初始化失败",
        ["InitFailedHint"] = "请检查网络连接和配置后重试。",
        ["CopyButton"] = "Copy",
        ["CopyDone"] = "Done",
        ["Refresh"] = "刷新",
        ["OpenDevUi"] = "Open in DevUI Browser",
        ["ReloadAll"] = "Reload All",
        ["LoadingJobs"] = "暂无后台作业。让 agent 跑一个 long-running shell 命令即可出现。",
        ["WorkflowPanel"] = "热改编排 (Hot-editable Workflows)",
        ["WorkflowHint"] = "提示：编辑 .livingtree/workflows/*.yaml 后保存即可热加载。",
        ["JobPanel"] = "后台作业 (Background Jobs)",

        // ── CLI ──
        ["CliAgentsList"] = "列出所有 agents",
        ["CliAgentsShow"] = "展示 agent 详情",
        ["CliMcpServer"] = "以 MCP server 模式启动",
        ["CliDefaultModel"] = "默认模型",
        ["CliActiveModel"] = "当前模型",
        ["CliUnknownCommand"] = "未知命令",

        // ── Agent / System Prompt ──
        ["SystemPromptIntro"] = "你是 LTAI 助手，使用工具完成用户的请求。",
        ["SystemPromptRules"] = """
- 连续调用 4 次以上工具前必须先向用户说明你正在做什么。
- 如果工具调用失败或返回异常，**调整策略**而不是重试同一个调用。
- 回复应简洁准确，使用中文。
- 如果用户的请求需要复杂推理或多步骤规划，使用 Plan 工具制定计划并获得批准后再执行。
- 在回复中输出 `<<<NEEDS_PRO: <原因>>>` 标记请求升级到 L2 深度推理模型（长江苦力三号）。
""",
        ["SystemPromptBackground"] = """
你可以将任务**异步委派**给以下 sibling agents。每个 agent 在自己 session 中并发执行。
- 调用 `BackgroundAgents_StartTask` 启动后台任务（不阻塞，可连续启动多个）
- 调用 `BackgroundAgents_WaitForFirstCompletion` 等待任意一个完成
- 调用 `BackgroundAgents_GetTaskResults` 取出已完成的文本结果
- 调用 `BackgroundAgents_GetAllTasks` 列出所有任务
- 调用 `BackgroundAgents_ContinueTask` 向已完成任务的 session 追加输入
- 调用 `BackgroundAgents_ClearCompletedTask` 释放已完成的 session 节省内存
- 重要：取完结果后用 ClearCompletedTask 清理，除非还要 ContinueTask
""",

        // ── Greeting ──
        ["GreetingHello"] = "你好 👋 我是 LTAI 助手！我可以帮你编程、搜索、处理数据等。有什么需要帮忙的吗？",
        ["GreetingThanks"] = "不客气 😊 还有什么需要帮忙的吗？",
        ["GreetingFarewell"] = "再见 👋 随时欢迎回来！",
        ["GreetingProbing"] = "我是 LTAI 助手，我可以帮你：\n- 编写和调试代码\n- 搜索和分析数据\n- 处理文档和办公文件\n- 使用工具查询信息\n\n请告诉我你需要什么帮助？",
        ["GreetingGarbage"] = "我没太明白你的意思。请告诉我你需要什么帮助？",
        ["GreetingAffirmation"] = "好的，有什么需要帮忙的吗？",
    };

    private static readonly Dictionary<string, string> _en = new()
    {
        // ── General ──
        ["AppName"] = "LTAI Assistant",
        ["Loading"] = "Loading…",
        ["Ready"] = "Ready",
        ["Error"] = "Error",
        ["Success"] = "Success",
        ["Cancel"] = "Cancel",
        ["Confirm"] = "Confirm",
        ["Save"] = "Save",
        ["Delete"] = "Delete",
        ["Close"] = "Close",
        ["Retry"] = "Retry",
        ["Back"] = "Back",
        ["Next"] = "Next",
        ["Reset"] = "Reset",
        ["Unavailable"] = "Unavailable",
        ["Unknown"] = "Unknown",
        ["NotFound"] = "Not found",

        // ── TUI ──
        ["NewSessionCreated"] = "New session created",
        ["SessionCleared"] = "Session cleared",
        ["Exiting"] = "Exiting…",
        ["HelpTitle"] = "Help",
        ["StatusTitle"] = "Status",
        ["ModelTitle"] = "Model Management",
        ["CostTitle"] = "Cost",
        ["MemoryTitle"] = "Memory",
        ["SnippetTitle"] = "Snippets",
        ["WorkflowTitle"] = "Workflows",
        ["JobsTitle"] = "Jobs",
        ["PipelineTitle"] = "Pipeline",
        ["NoSnippets"] = "No snippets yet",
        ["SnippetSaved"] = "Snippet saved",
        ["SnippetDeleted"] = "Snippet deleted",
        ["SnippetNotFound"] = "Snippet not found",
        ["SnippetUsage"] = "Usage: /snippet save <key> <text>",
        ["JobRunning"] = "Running",
        ["JobCompleted"] = "Completed",
        ["JobCancelled"] = "Cancelled",
        ["JobFailed"] = "Failed",
        ["NoJobs"] = "No background jobs",
        ["JobsCount"] = "{0} jobs ({1} running)",
        ["WorkflowCount"] = "{0} workflows",
        ["LastReload"] = "Last reload",
        ["NoWorkflows"] = "No workflows",

        // ── Desktop ──
        ["InitFailed"] = "Service initialization failed",
        ["InitFailedHint"] = "Please check your network connection and configuration.",
        ["CopyButton"] = "Copy",
        ["CopyDone"] = "Done",
        ["Refresh"] = "Refresh",
        ["OpenDevUi"] = "Open in DevUI Browser",
        ["ReloadAll"] = "Reload All",
        ["LoadingJobs"] = "No background jobs yet. Ask an agent to run a long-running shell command.",
        ["WorkflowPanel"] = "Hot-editable Workflows",
        ["WorkflowHint"] = "Tip: edit .livingtree/workflows/*.yaml and save to hot-reload.",
        ["JobPanel"] = "Background Jobs",

        // ── CLI ──
        ["CliAgentsList"] = "List all agents",
        ["CliAgentsShow"] = "Show agent details",
        ["CliMcpServer"] = "Start in MCP server mode",
        ["CliDefaultModel"] = "Default model",
        ["CliActiveModel"] = "Active model",
        ["CliUnknownCommand"] = "Unknown command",

        // ── Agent / System Prompt ──
        ["SystemPromptIntro"] = "You are LTAI Assistant, using tools to fulfill user requests.",
        ["SystemPromptRules"] = """
- Before calling more than 4 tools in a row, explain what you are doing to the user.
- If a tool call fails or returns an exception, **adjust your strategy** instead of retrying the same call.
- Keep responses concise and accurate.
- For complex tasks requiring multi-step planning, use the Plan tool to create a plan and get approval before executing.
- Output `<<<NEEDS_PRO: <reason>>>` to request upgrade to L2 深度推理模型（长江苦力三号） model.
""",
        ["SystemPromptBackground"] = """
You can **asynchronously delegate** tasks to the following sibling agents. Each agent runs in its own session.
- Call `BackgroundAgents_StartTask` to start a background task (non-blocking)
- Call `BackgroundAgents_WaitForFirstCompletion` to wait for any task to finish
- Call `BackgroundAgents_GetTaskResults` to retrieve completed results
- Call `BackgroundAgents_GetAllTasks` to list all tasks
- Call `BackgroundAgents_ContinueTask` to continue a completed session
- Call `BackgroundAgents_ClearCompletedTask` to free memory
- Important: ClearCompletedTask after retrieving results, unless you need ContinueTask
""",

        // ── Greeting ──
        ["GreetingHello"] = "Hi! I'm LTAI Assistant! I can help you code, search, process data, and more. What can I help you with?",
        ["GreetingThanks"] = "You're welcome! 😊 Is there anything else I can help you with?",
        ["GreetingFarewell"] = "Goodbye! 👋 Feel free to come back anytime!",
        ["GreetingProbing"] = "I'm LTAI Assistant. I can help you:\n- Write and debug code\n- Search and analyze data\n- Process documents and office files\n- Query information using tools\n\nWhat do you need help with?",
        ["GreetingGarbage"] = "I didn't quite catch that. What can I help you with?",
        ["GreetingAffirmation"] = "OK, what can I help you with?",

        // ── TUI ──
        ["You"] = "You",
        ["AI"] = "AI",
        ["Tool"] = "Tool",
        ["Error"] = "Error",
        ["System"] = "System",
        ["MemList"] = "Memory List",
        ["MemSearch"] = "Search Memory",
        ["MemDelete"] = "Delete Memory",
        ["MemStats"] = "Memory Stats",
        ["NoMemory"] = "No memories stored",
        ["FileBrowser"] = "File Browser",
        ["FullScreenEdit"] = "Full-Screen Edit",
        ["Dashboard"] = "Dashboard",
        ["Chat"] = "Chat",
        ["Config"] = "Config",
        ["Skill"] = "Skills",
        ["SessionMgr"] = "Sessions",
        ["JobMgr"] = "Jobs",
        ["MemBrowser"] = "Memory",
        ["WorkflowVis"] = "Workflows",
        ["GraphBrowse"] = "Graph",
        ["Exit"] = "Exit",
        ["View"] = "View",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> _all = new()
    {
        ["zh-CN"] = _zh,
        ["en-US"] = _en,
    };

    private static string _currentLang;

    static Locale()
    {
        _currentLang = DetectLanguage();
    }

    /// <summary>Detect OS language, returns "zh-CN" or "en-US".</summary>
    public static string DetectLanguage()
    {
        var ui = CultureInfo.CurrentUICulture;
        if (ui.TwoLetterISOLanguageName == "zh") return "zh-CN";
        return "en-US";
    }

    /// <summary>Get a localized string by key. Falls back to zh-CN then raw key.</summary>
    public static string Get(string key)
    {
        if (_all.TryGetValue(_currentLang, out var dict) && dict.TryGetValue(key, out var val))
            return val;
        if (_zh.TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    /// <summary>Get a formatted localized string.</summary>
    public static string Format(string key, params object?[] args)
    {
        return string.Format(Get(key), args);
    }

    /// <summary>Get current language code.</summary>
    public static string CurrentLang => _currentLang;

    /// <summary>Switch language at runtime (TUI /lang command).</summary>
    public static void SetLang(string lang)
    {
        if (_all.ContainsKey(lang)) _currentLang = lang;
    }

    /// <summary>True if current language is Chinese.</summary>
    public static bool IsChinese => _currentLang == "zh-CN";

    /// <summary>
    /// D2: Resolve a greeting template variable for YAML workflows.
    /// Called from YAML via PowerFx expression: =Locale("GreetingHello")
    /// </summary>
    public static string T(string key) => Get(key);
}
