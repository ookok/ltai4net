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

    static Node? ResolveModel(string[] stack)
    {
        if (stack.Length == 0)
            return new(new[] { "l0  嵌入模型", "l1  对话模型", "l2  推理模型" },
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
            return new(SlashCommands.KnownProviders.Keys.ToArray(), picked => picked);

        if (stack[0] == "l0" && stack.Length == 2 && stack[1] == "api" && stack.Length == 2)
            return new(SlashCommands.KnownProviders.Where(kv => !string.IsNullOrEmpty(kv.Value.Endpoint))
                .Select(kv => kv.Key).ToArray(), picked => picked);

        return null;
    }

    static Node? ResolveGeneric(string cmd, string[] stack)
    {
        if (stack.Length > 0) return null;
        return cmd switch
        {
            "snippet" => new(new[] { "list   列出全部", "save   保存常用语", "use    使用常用语", "delete  删除常用语", "rename  重命名", "edit    编辑" }, p => p),
            "workflow" => new(new[] { "list    列出", "reload  重载", "show    查看", "open    打开" }, p => p),
            "pipe"    => new(new[] { "list    列出预设", "run     运行", "stop    停止" }, p => p),
            "jobs"    => new(new[] { "list    列出", "watch   监视", "cancel  取消", "show    详情" }, p => p),
            "lang"    => new(new[] { "zh-CN   简体中文", "en-US   English" }, p => p),
            "mode"    => new(new[] { "review  审查模式", "auto    自动模式" }, p => p),
            _ => null
        };
    }
}
