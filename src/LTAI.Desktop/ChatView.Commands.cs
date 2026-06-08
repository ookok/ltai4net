using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using LTAI.Core.Commands;

namespace LTAI.Desktop;

public sealed partial class ChatView : UserControl
{
    private async Task HandleSlashCommandAsync(string input)
    {
        var cmd = _cmdService.Parser.Parse(input);
        if (cmd is EmptyCommand or ChatMessageCommand)
            return;

        // Commands with custom rendering — dispatch to view-specific methods
        switch (cmd)
        {
            case HelpCommand:
                ShowHelp();
                return;
            case StatusCommand:
                ShowStatus();
                return;
            case ModelsCommand:
                ShowModels();
                return;
            case ModelCommand m:
                ShowModel(m.Args);
                return;
            case SnippetCommand s:
                if (string.IsNullOrWhiteSpace(s.Args)) { await ShowCmdPickerAsync("snippet"); return; }
                await HandleSnippetCommandAsync(s.Args).ConfigureAwait(false);
                return;
            case ConfigCommand c:
                if (string.IsNullOrWhiteSpace(c.Args)) { AddSystemBubble("用法: /config apikey|export|import"); return; }
                HandleConfigDesktop(c.Args);
                return;
            case NewSessionCommand:
                _ = ResetSessionAsync();
                return;
            case GraphCommand { Args: "" or null }:
            case GraphCommand { Args: "init" }:
                AddSystemBubble("🔨 Building code graph + document index...");
                _ = GraphInitAsync();
                return;
            case GraphCommand { Args: not null } g when g.Args.StartsWith("search"):
                var q = g.Args.Length > 7 ? g.Args[7..].Trim() : "";
                if (string.IsNullOrWhiteSpace(q)) { AddSystemBubble("Usage: /graph search <query>"); return; }
                AddSystemBubble($"🔍 Searching graph for: {q}");
                _ = GraphSearchAsync(q);
                return;
            case ExitCommand:
                (TopLevel.GetTopLevel(this) as Window)?.Close();
                return;
        }

        // All other commands — route through DesktopCommandService
        var result = _cmdService.Execute(input);
        if (result.RequestExit)
            (TopLevel.GetTopLevel(this) as Window)?.Close();
        else if (result.ClearMessages)
            _ = ResetSessionAsync();
        else if (result.StatusMessage != null)
            AddSystemBubble(result.StatusMessage);
    }

    private static bool CmdHasLevel1(string cmd) => cmd switch
    {
        "model" or "snippet" or "workflow" or "pipe" or "jobs" or "lang" or "mode" => true,
        _ => false
    };

    private static string[] CmdLevel1Items(string cmd) => cmd switch
    {
        "model" => new[] { "l0  嵌入模型", "l1  对话模型", "l2  推理模型" },
        "snippet" => new[] { "list  列出全部", "save  保存常用语", "use   使用常用语", "delete 删除常用语", "rename 重命名", "edit   编辑" },
        "workflow" => new[] { "list   列出", "reload 重载", "show   查看", "open   打开" },
        "pipe" => new[] { "list  列出预设", "run   运行", "stop  停止" },
        "jobs" => new[] { "list   列出", "watch  监视", "cancel 取消", "show   详情" },
        "lang" => new[] { "zh-CN  简体中文", "en-US  English" },
        "mode" => new[] { "review 审查模式", "auto   自动模式" },
        _ => Array.Empty<string>()
    };

    private void ShowModels()
    {
        var lines = new List<string>();
        var embedder = _svc.Services.GetService(typeof(LTAI.AI.LocalEmbedder)) as LTAI.AI.LocalEmbedder;
        if (embedder?.Available == true)
            lines.Add($"L0 嵌入 (optional): {embedder.CurrentModelName} ({embedder.Dim}d)");
        else
            lines.Add("L0 嵌入 (optional): 未加载");
        var opts = (_svc.Services.GetService(typeof(Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>))
            as Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>)?.Value;
        var l1Cfg = opts?.AI.L1;
        var l2Cfg = opts?.AI.L2;
        if (l1Cfg != null && !string.IsNullOrEmpty(l1Cfg.Provider))
            lines.Add($"L1 标准 (required): {l1Cfg.Provider} / {l1Cfg.Model}");
        else
            lines.Add("L1: 未配置 (/model l1)");
        if (l2Cfg != null && !string.IsNullOrEmpty(l2Cfg.Provider))
            lines.Add($"L2 深度 (optional): {l2Cfg.Provider} / {l2Cfg.Model}");
        else
            lines.Add("L2 (optional): 未配置，由 L1 替代");
        AddSystemBubble(string.Join("\n", lines));
    }

    private void ShowModel(string args)
    {
        var embedder = _svc.Services.GetService(typeof(LTAI.AI.LocalEmbedder)) as LTAI.AI.LocalEmbedder;
        if (string.IsNullOrWhiteSpace(args))
        {
            ShowModels();
            return;
        }
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts[0].ToLowerInvariant();
        if (sub is "l1" or "l2")
        {
            if (parts.Length == 1)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"配置 {sub.ToUpperInvariant()}: /model {sub} <provider>");
                sb.AppendLine("可用 Provider:");
                foreach (var k in new[] { "DeepSeek", "SiliconFlow", "Aliyun(Qwen)", "Zhipu(GLM)", "OpenAI", "Anthropic", "Ollama", "LMStudio", "vLLM" })
                    sb.AppendLine($"  · {k}");
                AddSystemBubble(sb.ToString());
                return;
            }
            AddSystemBubble($"{sub.ToUpperInvariant()}: {parts[1]}\n输入 /model {sub} {parts[1]} <模型名> 完成配置");
            return;
        }
        var lines = new List<string>();
        if (embedder?.Available == true) lines.Add($"L0: {embedder.CurrentModelName} ({embedder.Dim}d)");
        AddSystemBubble(string.Join("\n", lines));
    }

    private void HandleConfigDesktop(string args)
    {
        var s = args.Trim();
        if (s.StartsWith("export"))
            DoConfigExport();
        else if (s.StartsWith("import"))
            DoConfigImport();
        else
            AddSystemBubble("用法: /config apikey|export|import");
    }

    private void DoConfigExport()
    {
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, ".livingtree");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"config-export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var src = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(src)) src = Path.Combine(Environment.CurrentDirectory, "appsettings.json");
            if (File.Exists(src))
            {
                File.Copy(src, path, overwrite: true);
                AddSystemBubble($"[green]✅ 配置已导出 → {path}[/]");
            }
            else AddSystemBubble("[red]找不到 appsettings.json[/]");
        }
        catch (Exception ex) { AddSystemBubble($"[red]导出失败: {ex.Message}[/]"); }
    }

    private void DoConfigImport()
    {
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, ".livingtree");
            var files = Directory.GetFiles(dir, "config-export-*.json");
            if (files.Length == 0)
            {
                AddSystemBubble("[yellow]未找到配置文件 (.livingtree/config-export-*.json)[/]");
                return;
            }
            Array.Sort(files);
            var latest = files[^1];
            var dest = Path.Combine(Environment.CurrentDirectory, "appsettings.json");
            File.Copy(latest, dest, overwrite: true);
            AddSystemBubble($"[green]✅ 已导入: {Path.GetFileName(latest)}[/]");
        }
        catch (Exception ex) { AddSystemBubble($"[red]导入失败: {ex.Message}[/]"); }
    }

    private async Task ShowCmdPickerAsync(string cmd)
    {
        try
        {
            var items = CmdLevel1Items(cmd);
            if (items.Length == 0) return;
            var owner = this.VisualRoot as Window;
            if (owner == null) return;
            var dialog = new Dialogs.CommandPickerDialog($"/{cmd}", items);
            await dialog.ShowDialog(owner).ConfigureAwait(false);
            if (dialog.Selected != null)
            {
                _input.Text = $"/{cmd} {dialog.Selected}";
                _input.CaretIndex = _input.Text.Length;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatView] Build context failed: {ex.Message}"); }
    }

    private void ShowHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("可用命令：");
        sb.AppendLine();
        sb.AppendLine("/help         — 显示此帮助");
        sb.AppendLine("/new          — 新建会话");
        sb.AppendLine("/exit         — 退出应用");
        sb.AppendLine("/status       — 显示统计信息");
        sb.AppendLine("/models       — 显示 L0/L1/L2 当前模型");
        sb.AppendLine("/model l1|l2  — 配置 L1/L2");
        sb.AppendLine("/pwd          — 显示当前目录");
        sb.AppendLine("/ls           — 列出当前目录");
        sb.AppendLine("/cd <路径>    — 切换工作目录");
        sb.AppendLine("/git          — Git: status|diff|log|add|commit");
        sb.AppendLine("/cost         — 显示本轮预估费用");
        sb.AppendLine("/plan         — 查看计划状态");
        sb.AppendLine("/approve      — 批准当前计划");
        sb.AppendLine("/undo         — 撤销上次编辑");
        sb.AppendLine("/mode         — review|auto 编辑模式");
        sb.AppendLine("/lang         — 切换 zh-CN|en-US");
        sb.AppendLine("/snippet      — 常用语: list|save|use|delete|rename|edit");
        sb.AppendLine("/spec         — 开发流程: list|new|show|delete|plan|tasks");
        sb.AppendLine("/retry        — 重发上一条消息");
        sb.AppendLine("/compact      — 压缩汇总历史消息");
        sb.AppendLine("/skill        — 运行技能");
        sb.AppendLine("/sessions     — list|load|delete|export <name> [md|json|html]|import <path>");
        sb.AppendLine("── 桌面端专用 ──");
        sb.AppendLine("/jobs         — 打开作业面板 (Ctrl+7)");
        sb.AppendLine("/workflow     — 打开工作流面板 (Ctrl+6)");
        sb.AppendLine("/memory       — 记忆浏览器");
        sb.AppendLine("/graph        — 知识图谱浏览器");
        sb.AppendLine("── 命令别名 ──");
        sb.AppendLine("/pipe         — 管道执行");
        sb.AppendLine("/tools        — 工具列表");
        sb.AppendLine("/config export— 导出配置");
        sb.AppendLine("/config import— 导入配置");
        AddSystemBubble(sb.ToString().TrimEnd());
    }
}
