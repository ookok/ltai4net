// 级联菜单路由表 — 纯数据，不含 UI 渲染

namespace LTAI.TUI;

public static class CascadeRoutes
{
    public record Node(string[] Items, Func<string, string?> Select);

    public static Node? Resolve(string cmd, string[] stack)
    {
        if (cmd == "model") return ResolveModel(stack);
        return ResolveGeneric(cmd, stack);
    }

    /// <summary>When a leaf node is selected, if this returns non-null the cascade
    /// will prompt for free-text input instead of just setting PendingInput.</summary>
    public static string? GetLeafPrompt(string cmd, string[] stack)
    {
        if (stack.Length == 0) return null;
        var action = stack[^1];
        return (cmd, action) switch
        {
            ("graph", "search") => "[yellow]输入搜索关键词:[/]",
            ("snippet", "save") => "[yellow]输入常用语名称:[/]",
            ("snippet", "use" or "delete" or "edit") => "[yellow]输入常用语名称:[/]",
            ("snippet", "rename") => "[yellow]输入常用语原名称:[/]",
            ("workflow", "show" or "open") => "[yellow]输入工作流名称:[/]",
            ("workflow", "reload") => "[yellow]输入工作流名称(空=全部重载):[/]",
            ("model", "download" or "delete") when stack.Length == 2 => null, // already has dynamic list
            // ── 三级级联: l0 api <provider> 后输入模型名 ──
            ("model", _) when stack is ["l0", "api", _] => "[yellow]输入嵌入模型名称 (或直接 Enter 查看列表):[/]",
            // ── l1/l2 + <provider>: cascade closes → HandleLayerSelectComplete fetches model list via API → model picker ──
            ("model", _) when stack is ["l1", _] or ["l2", _] => null,
            // ── config apikey: 选择 provider 后输入 key ──
            ("config", "apikey") when stack.Length == 1 => null, // next level shows provider list
            ("config", _) when stack is ["apikey", _] => "[yellow]输入 API Key:[/]",
            // ── spec cascade ──
            ("spec", "new") when stack.Length == 1 => "[yellow]输入 spec 名称:[/]",
            ("spec", "show" or "edit" or "delete" or "plan" or "tasks") when stack.Length == 1 => "[yellow]输入 spec 名称:[/]",
            ("spec", "status") when stack.Length == 1 => "[yellow]输入 spec 名称:[/]",
            _ => null,
        };
    }

    static Node? ResolveModel(string[] stack)
    {
        if (stack.Length == 0)
            return new(new[] { "l0  向量模型（长江苦力一号）", "l1  快速反应模型（长江苦力二号）", "l2  深度推理模型（长江苦力三号）" },
                picked => picked == "l0" ? "l0" : picked);

        if (stack[0] == "l0")
        {
            if (stack.Length == 1)
                return new(new[] { "list    已下载", "download 下载", "switch   切换", "delete   删除", "info     详情", "cleanup  清理", "quant    量化" }, picked => picked);

            if (stack[1] is "download" or "switch" or "delete" && stack.Length == 2)
            {
                var models = LTAI.AI.LocalEmbedder.ListAvailableModels();
                return new(models.Where(m => m.Downloaded || stack[1] == "download")
                    .Select(m => $"{m.Id}  {m.DisplayName}").ToArray(), picked => picked);
            }
        }

        if (stack[0] is "l1" or "l2" && stack.Length == 1)
            return new(ProviderHelpers.KnownProviders.Keys.ToArray(), picked => picked);

        if (stack[0] == "l0" && stack.Length == 2 && stack[1] == "api" && stack.Length == 2)
            return new(ProviderHelpers.KnownProviders.Where(kv => !string.IsNullOrEmpty(kv.Value.Endpoint))
                .Select(kv => kv.Key).ToArray(), picked => picked);

        return null;
    }

    static Node? ResolveGeneric(string cmd, string[] stack)
    {
        // ── config apikey → provider list ──
        if (cmd == "config" && stack.Length == 1 && stack[0] == "apikey")
            return new(ProviderHelpers.KnownProviders
                .Where(kv => !string.IsNullOrEmpty(kv.Value.EnvVar))
                .Select(kv => kv.Key).ToArray(), picked => picked);

        // ── config clear → provider list ──
        if (cmd == "config" && stack.Length == 1 && stack[0] == "clear")
            return new(ProviderHelpers.KnownProviders
                .Where(kv => !string.IsNullOrEmpty(kv.Value.EnvVar))
                .Select(kv => kv.Key).ToArray(), picked => picked);

        if (stack.Length > 0) return null;
        return cmd switch
        {
            "snippet" => new(new[] { "list   列出全部", "save   保存常用语", "use    使用常用语", "delete  删除常用语", "rename  重命名", "edit    编辑" }, p => p),
            "workflow" => new(new[] { "list    列出", "reload  重载", "show    查看", "open    打开" }, p => p),
            "pipe"    => new(new[] { "list    列出预设", "run     运行", "stop    停止" }, p => p),
            "jobs"    => new(new[] { "list    列出", "watch   监视", "cancel  取消", "show    详情" }, p => p),
            "lang"    => new(new[] { "zh-CN   简体中文", "en-US   English" }, p => p),
            "mode"    => new(new[] { "review  审查模式", "auto    自动模式" }, p => p),
            "agents"  => new(new[] { "list    列出 Agent", "show    查看详情" }, p => p),
            "tools"   => new(new[] { "list    列出全部", "domain  按域筛选" }, p => p),
            "mcp"     => new(new[] { "list    列出服务器", "status  连接状态", "tools   MCP 工具列表" }, p => p),
            "spec"    => new(new[] { "list    全部列表", "new     新建", "show    查看内容", "edit    编辑内容", "delete  删除", "status  修改状态", "plan    实现计划", "tasks   拆解任务" }, p => p),
            "config"  => new(new[] {
                "status   查看配置状态",
                "apikey   设置 API Key",
                "provider 选择 Provider",
                "export   导出配置",
                "import   导入配置",
                "clear    清除 API Key",
                "clear-all 清除全部"
            }, p => p),
            _ => null
        };
    }
}
