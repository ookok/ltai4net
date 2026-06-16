using System.Diagnostics;
using System.Text;
using LTAI.Core.Configuration;
using LTAI.Core.Commands;
using LTAI.Core.Specs;
using LTAI.AI;
using LTAI.Agent.Tools;

namespace LTAI.Desktop.Services;

public sealed class DesktopCommandService
{
    private readonly CommandParser _parser = new();
    private readonly SpecService _specs = new(Path.Combine(AppContext.BaseDirectory, ".livingtree", "specs"));

    public CommandParser Parser => _parser;

    public DesktopCommandResult Execute(string input)
    {
        var cmd = _parser.Parse(input);

        return cmd switch
        {
            EmptyCommand => new(null),
            ChatMessageCommand => new(null),
            UnknownCommand u => new($"⚠️ 未知命令 '{u.CmdName}'" +
                (u.Suggestion != null ? $"，你要找的是 /{u.Suggestion} 吗？" : "")),
            ExitCommand => new("👋 退出中...", RequestExit: true),
            NewSessionCommand => new("✅ 新会话已创建", ClearMessages: true),
            HelpCommand => new(null),  // handled by ChatView rendering
            StatusCommand => new(null), // handled by ChatView rendering
            CostCommand => new(BuildCost()),
            PwdCommand => new($"📁 当前目录: {Directory.GetCurrentDirectory()}"),
            PlanCommand => new(PlanTools.PlanStatus()),
            ApproveCommand => new(PlanTools.ApprovePlan() + "\n" + PlanTools.StartExecution()),
            LsCommand { Args: var a } => new(ListDir(a)),
            CdCommand { Args: var a } => new(ChangeDir(a)),
            ModelCommand _ => new(null), // handled by ChatView rendering
            ModelsCommand => new(null),  // handled by ChatView rendering
            ConfigCommand => new(null),  // handled by ChatView rendering
            SnippetCommand _ => new(null), // handled by ChatView rendering
            GitCommand { Args: var a } => new(HandleGit(a)),
            ModeCommand { Args: "" } => new("⚠️ 用法: /mode <auto|review>"),
            ModeCommand { Args: not null } m => new(HandleMode(m.Args)),
            LangCommand { Args: "" } => new("⚠️ 用法: /lang <zh|en>"),
            LangCommand { Args: not null } l => new(HandleLang(l.Args)),
            UndoCommand => new("🔄 已发送撤销请求，请等待..."),
            RetryCommand => new("🔄 已发送重试请求，请等待..."),
            CompactCommand => new("📦 已发送压缩请求，正在汇总..."),
            JobsCommand { Args: var a } => new($"📋 作业 {(string.IsNullOrEmpty(a) ? "列表" : a)} — 请查看作业面板 (Ctrl+7)"),
            WorkflowCommand { Args: var a } => new($"🔁 工作流 {(string.IsNullOrEmpty(a) ? "列表" : a)} — 请查看工作流面板 (Ctrl+6)"),
            PipeCommand { Args: var a } => new(HandlePipe(a)),
            SkillCommand { Args: var a } => new($"⚡ 技能 {(string.IsNullOrEmpty(a) ? "列表" : a)} — 请查看技能面板 (Ctrl+4)"),
            McpCommand { Args: var a } => new(HandleMcp(a)),
            AgentsCommand { Args: var a } => new(HandleAgents(a)),
            ToolsCommand { Args: var a } => new(HandleTools(a)),
            ThemeCommand { Args: var a } => new(HandleTheme(a)),
            SpecCommand { Args: var a } => new(HandleSpec(a)),
            // New: Desktop prompt editor, key bindings, file commands, orchestration
            PromptCommand => new("📝 Prompt 编辑器已打开 (Ctrl+Shift+P)"),
            KeysCommand => new(BuildKeyBindings()),
            FileCommand { Args: var a } => new(HandleFile(a)),
            OrchestrationCommand { Args: var a } => new($"🎭 编配 {(string.IsNullOrEmpty(a) ? "状态" : a)} — {PlanTools.PlanStatus()}"),
            _ => new(null),
        };
    }

    private static string HandleMcp(string args)
    {
        if (string.IsNullOrWhiteSpace(args) || args == "list")
        {
            var mcp = App.Ltais?.Services.GetService(typeof(LTAI.Agent.Mcp.McpClientFactory));
            return mcp != null ? "✅ MCP 客户端已就绪" : "⚠️ MCP 未配置。请在 appsettings.json 的 LTAI:Mcp:Servers 中配置";
        }
        return "用法: /mcp list|status";
    }

    private static string HandleAgents(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        var name = parts.Length > 1 ? parts[1] : "";
        return sub switch
        {
            "list" or "" => "已注册 Agents: LTAI-Chat, LTAI-Code, LTAI-Data, LTAI-Math, LTAI-Writer, LTAI-LLM, LTAI-System, LTAI-Frontend, LTAI-Chat-Pro, sql-agent",
            "show" when !string.IsNullOrEmpty(name) => $"Agent '{name}' 详情请查看 DevUI 面板 (Ctrl+1)",
            _ => "用法: /agents list|show <name>",
        };
    }

    private static string HandleTools(string args)
    {
        var all = ToolRegistry.AllTools;
        if (all.Count == 0) return "暂无已注册工具";
        var groups = all.GroupBy(t => string.IsNullOrEmpty(t.Domain) ? "default" : t.Domain).OrderBy(g => g.Key);
        var sb = new StringBuilder();
        sb.AppendLine($"已注册工具 ({all.Count}):\n");
        foreach (var g in groups)
        {
            sb.AppendLine($"[{g.Key}]");
            foreach (var t in g.Take(5))
                sb.AppendLine($"  · {t.Name} — {t.Description}");
            if (g.Count() > 5) sb.AppendLine($"  ... 还有 {g.Count() - 5} 个");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string HandleTheme(string args)
    {
        Desktop.LtaiTheme.Toggle();
        var mode = Desktop.LtaiTheme.Current == AppTheme.Light ? "浅色" : "深色";
        return $"🎨 已切换为 {mode} 主题";
    }

    private static string HandlePipe(string args)
    {
        if (string.IsNullOrWhiteSpace(args) || args == "list")
            return "🔀 管道功能 — 请通过 YAML 编排使用。\n可用命令: /pipe list|run <name>";
        if (args.StartsWith("run "))
        {
            var name = args["run ".Length..];
            return $"🔄 正在执行管道 '{name}'... (通过命令行触发)\n结果请查看作业面板";
        }
        return "用法: /pipe list|run <name>";
    }

    private static string BuildCost()
    {
        var usage = UsageTracker.Summary();
        return string.IsNullOrEmpty(usage)
            ? $"Token: {UsageTracker.TotalTokens:N0} | 请求: {UsageTracker.Requests} | 缓存: {UsageTracker.CacheHitRate:F1}%"
            : usage;
    }

    private static string ListDir(string path)
    {
        try
        {
            var dir = !string.IsNullOrWhiteSpace(path)
                ? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path))
                : Directory.GetCurrentDirectory();

            if (!Directory.Exists(dir)) return $"❌ 目录不存在: {dir}";

            var entries = Directory.GetFileSystemEntries(dir)
                .Take(30)
                .Select(e =>
                {
                    var name = Path.GetFileName(e);
                    return Directory.Exists(e) ? $"📁 {name}" : $"📄 {name}";
                });
            return $"📂 {dir}\n" + string.Join("\n", entries);
        }
        catch (Exception ex) { return $"❌ 列目录失败: {ex.Message}"; }
    }

    private static string HandleGit(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return "⚠️ 用法: /git status|diff|log|add|commit|pull|push";
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = new Process { StartInfo = psi };
            p.Start();
            p.WaitForExit(60_000);
            var output = p.StandardOutput.ReadToEnd().Trim();
            var error = p.StandardError.ReadToEnd().Trim();
            var result = p.ExitCode == 0
                ? $"✅ git {args}\n\n{output}"
                : $"❌ git {args}\n\n{error}";
            return result.Trim();
        }
        catch (Exception ex) { return $"❌ git 错误: {ex.Message}"; }
    }

    private static string ChangeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"📁 当前目录: {Directory.GetCurrentDirectory()}";
        try
        {
            var newDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            if (!Directory.Exists(newDir)) return $"❌ 目录不存在: {newDir}";
            Directory.SetCurrentDirectory(newDir);
            return $"📂 {newDir}";
        }
        catch (Exception ex) { return $"❌ 切换失败: {ex.Message}"; }
    }

    private string HandleSpec(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1] : "";

        return sub switch
        {
            "" or "list" => HandleSpecList(),
            "new" or "create" => HandleSpecNew(subArgs),
            "show" or "read" => HandleSpecShow(subArgs),
            "delete" or "rm" => HandleSpecDelete(subArgs),
            "plan" => HandleSpecSub("plan", subArgs),
            "tasks" => HandleSpecSub("tasks", subArgs),
            _ => "用法: /spec list|new|show|delete|plan|tasks",
        };
    }

    private string HandleSpecList()
    {
        var list = _specs.List();
        if (list.Count == 0) return "暂无 spec。使用 /spec new <name> 创建";
        return string.Join("\n", list.Select(m => $"📄 {m.Name} [{m.Status}] — {m.Description}"));
    }

    private string HandleSpecNew(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "用法: /spec new <name>";
        if (_specs.Get(name) != null) return $"spec '{name}' 已存在";
        _specs.WriteSpec(name, $"# {name}\n\n## 概述\n\n## 功能需求\n\n## 验收标准\n");
        return $"✅ spec '{name}' 已创建。使用 /spec show {name} 查看";
    }

    private string HandleSpecShow(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "用法: /spec show <name>";
        var content = _specs.ReadSpec(name);
        if (content == null) return $"spec '{name}' 未找到";
        return content;
    }

    private string HandleSpecDelete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "用法: /spec delete <name>";
        return _specs.Delete(name) ? $"🗑️ spec '{name}' 已删除" : $"spec '{name}' 未找到";
    }

    private string HandleSpecSub(string sub, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var all = _specs.List().Where(m => m.Status >= LTAI.Core.Specs.SpecStatus.Planned).ToList();
            return all.Count == 0 ? $"尚无含 {sub} 的 spec" : string.Join("\n", all.Select(m => $"📄 {m.Name}"));
        }
        var content = sub == "plan" ? _specs.ReadPlan(name) : _specs.ReadTasks(name);
        return content ?? $"'{name}' 尚无 {sub}";
    }

    private static string BuildKeyBindings() =>
        """
        ⌨️ 快捷键一览:
          Ctrl+1   DevUI面板       Ctrl+5   工作流面板
          Ctrl+2   聊天面板         Ctrl+6   作业面板
          Ctrl+3   代码编辑器       Ctrl+7   记忆浏览器
          Ctrl+4   技能面板         Ctrl+8   图谱浏览器
          Enter    发送消息         Ctrl+Z   撤销
          Ctrl+U   清空输入         Esc      取消
          Ctrl+Shift+P  命令面板   Ctrl+O   打开文件
          Ctrl+F   搜索             F5/F6/F7 构建/运行/测试
          Ctrl+K   断点切换         F9       调试—继续
          F10      单步跳过         F11      单步进入
        """;

    private static string HandleMode(string args)
    {
        var mode = args.ToLowerInvariant() switch
        {
            "auto" => "auto",
            "review" => "review",
            _ => null
        };
        if (mode == null) return "⚠️ 模式: auto=自动, review=审查";
        return $"🔄 已切换模式: {mode}";
    }

    private static string HandleLang(string args)
    {
        var lang = args.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "cn" => "zh-CN",
            "en" or "en-us" or "us" => "en-US",
            _ => null
        };
        if (lang == null) return "⚠️ 用法: /lang zh|en";
        return $"🌐 已切换语言: {lang}";
    }

    private static string HandleFile(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var arg = parts.Length > 1 ? parts[1] : "";
        return sub switch
        {
            "list" or "ls" => ListDir(string.IsNullOrEmpty(arg) ? "." : arg),
            "read" => string.IsNullOrEmpty(arg) ? "用法: /file read <path>" : $"📄 {arg} (请使用编辑器打开)",
            "write" => "用法: /file write <path> <content>",
            _ => "用法: /file list <dir>|read <path>|write <path> <content>",
        };
    }
}
