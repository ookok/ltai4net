using System.Diagnostics;
using System.Text;
using LTAI.Core.Configuration;
using LTAI.Core.Commands;
using LTAI.Core.Specs;
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
            ModeCommand { Args: not null } m => new($"🔄 切换模式: {m.Args}"),
            LangCommand { Args: "" } => new("⚠️ 用法: /lang <zh|en>"),
            LangCommand { Args: not null } l => new($"🌐 已切换: {l.Args}"),
            UndoCommand => new("↩️ 撤销 — 请使用编辑工具 (Ctrl+Z)"),
            RetryCommand => new("🔄 重试 — 请重新发送上一条消息"),
            CompactCommand => new("📦 压缩 — 对话历史将被汇总压缩"),
            JobsCommand { Args: var a } => new($"📋 作业 {(string.IsNullOrEmpty(a) ? "列表" : a)} — 请查看作业面板 (Ctrl+7)"),
            WorkflowCommand { Args: var a } => new($"🔁 工作流 {(string.IsNullOrEmpty(a) ? "列表" : a)} — 请查看工作流面板 (Ctrl+6)"),
            PipeCommand { Args: var a } => new($"🔀 管道 {(string.IsNullOrEmpty(a) ? "列表" : a)} — 桌面端暂不支持"),
            SkillCommand { Args: var a } => new($"⚡ 技能 {(string.IsNullOrEmpty(a) ? "列表" : a)} — 请查看技能面板 (Ctrl+4)"),
            SpecCommand { Args: var a } => new(HandleSpec(a)),
            _ => new(null),
        };
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
            var output = p.StandardOutput.ReadToEnd().Trim();
            var error = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit(60_000);
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
}
