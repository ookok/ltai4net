using System.Text;
using Avalonia.Controls;

namespace LTAI.Desktop;

public sealed partial class ChatView : UserControl
{
    private async Task HandleSnippetCommandAsync(string args)
    {
        if (_snippetStore == null)
        {
            AddSystemBubble("⚠️ 常用语存储未初始化（需要 LTAI.Agent 服务）");
            return;
        }

        // Fallback: /snippet <key> → use <key>
        var cmd = LTAI.Agent.Snippets.SnippetCommandParser.Parse(args);
        if (cmd.Action == LTAI.Agent.Snippets.SnippetAction.Unknown)
        {
            var firstToken = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(firstToken))
            {
                var existing = await _snippetStore.GetAsync(firstToken).ConfigureAwait(false);
                if (existing != null)
                    cmd = new LTAI.Agent.Snippets.SnippetCommand(
                        LTAI.Agent.Snippets.SnippetAction.Use, firstToken, "", "", null);
            }
        }

        if (cmd.Error != null) { AddSystemBubble($"⚠️ {cmd.Error}"); return; }

        switch (cmd.Action)
        {
            case LTAI.Agent.Snippets.SnippetAction.List:
                await ShowSnippetListAsync();
                break;
            case LTAI.Agent.Snippets.SnippetAction.Save:
                await TrySaveSnippetAsync(cmd.Key, cmd.Content);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Use:
                await TryUseSnippetAsync(cmd.Key);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Delete:
                await TryDeleteSnippetAsync(cmd.Key);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Rename:
                await TryRenameSnippetAsync(cmd.Key, cmd.NewKey);
                break;
            case LTAI.Agent.Snippets.SnippetAction.Edit:
                await TrySaveSnippetAsync(cmd.Key, cmd.Content);
                break;
        }
    }

    private async Task<LTAI.Agent.Snippets.Snippet?> TryGetSnippetAsync(string key)
        => _snippetStore == null ? null : await _snippetStore.GetAsync(key).ConfigureAwait(false);

    private async Task ShowSnippetListAsync()
    {
        try
        {
            if (_snippetStore == null) return;
            var list = await _snippetStore.ListAsync().ConfigureAwait(false);
            if (list.Count == 0)
            {
                AddSystemBubble("📝 暂无常用语\n用法: /snippet save <key> <text>");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"📝 常用语 ({list.Count} 条):");
            foreach (var s in list)
            {
                var lastUsed = s.LastUsedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "从未";
                var desc = string.IsNullOrEmpty(s.Description) ? "" : $"  — {s.Description}";
                var preview = s.Content.Length > 30 ? s.Content[..30] + "..." : s.Content;
                sb.AppendLine($"  /{s.Key,-16}  {preview,-34}  使用:{s.UseCount,3}  {lastUsed}{desc}");
            }
            sb.AppendLine("\n用法: /snippet use <key>");
            AddSystemBubble(sb.ToString().TrimEnd());
        }
        catch (Exception ex) { AddSystemBubble($"❌ 错误: {ex.Message}"); }
    }

    private async Task TrySaveSnippetAsync(string key, string content)
    {
        try
        {
            if (_snippetStore == null) return;
            await _snippetStore.SaveAsync(new LTAI.Agent.Snippets.Snippet
            {
                Key = key,
                Content = content,
            }).ConfigureAwait(false);
            AddSystemBubble($"✅ 已保存常用语 /{key}（{content.Length} 字符）");
        }
        catch (Exception ex)
        {
            AddSystemBubble($"❌ {ex.Message}");
        }
    }

    private async Task TryUseSnippetAsync(string key)
    {
        try
        {
            if (_snippetStore == null) return;
            var snippet = await _snippetStore.GetAsync(key).ConfigureAwait(false);
            if (snippet == null)
            {
                AddSystemBubble($"❌ 找不到常用语 '/{key}'");
                return;
            }
            await _snippetStore.TouchAsync(key).ConfigureAwait(false);
            // D61: fill the input box (not auto-send)
            _input.Text = snippet.Content;
            _input.CaretIndex = snippet.Content.Length;
            AddSystemBubble($"✅ 已调出常用语 /{key}（{snippet.Content.Length} 字符）— 已填入输入框");
        }
        catch (Exception ex) { AddSystemBubble($"❌ 错误: {ex.Message}"); }
    }

    private async Task TryDeleteSnippetAsync(string key)
    {
        try
        {
            if (_snippetStore == null) return;
            var existing = await _snippetStore.GetAsync(key).ConfigureAwait(false);
            if (existing == null)
            {
                AddSystemBubble($"❌ 找不到常用语 '/{key}'");
                return;
            }
            var usedHint = existing.UseCount > 0 ? $"（已使用 {existing.UseCount} 次）" : "";
            var ok = await _snippetStore.DeleteAsync(key).ConfigureAwait(false);
            AddSystemBubble(ok ? $"✅ 已删除常用语 /{key} {usedHint}" : $"❌ 删除失败");
        }
        catch (Exception ex) { AddSystemBubble($"❌ 错误: {ex.Message}"); }
    }

    private async Task TryRenameSnippetAsync(string oldKey, string newKey)
    {
        try
        {
            if (_snippetStore == null) return;
            var ok = await _snippetStore.RenameAsync(oldKey, newKey).ConfigureAwait(false);
            AddSystemBubble(ok
                ? $"✅ 已重命名 /{oldKey} → /{newKey}"
                : $"❌ 找不到常用语 '/{oldKey}'");
        }
        catch (Exception ex)
        {
            AddSystemBubble($"❌ {ex.Message}");
        }
    }
}
