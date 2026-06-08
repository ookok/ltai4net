using LTAI.Agent.Snippets;
using LTAI.Core.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class SnippetCommandService : ICommandService
{
    private readonly SnippetStore? _snippetStore;

    public SnippetCommandService(SnippetStore? snippetStore)
    {
        _snippetStore = snippetStore;
    }

    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        LTAI.Core.Commands.SnippetCommand sc => HandleSnippetCommandAsync(sc.Args),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private async Task<CommandResult> HandleSnippetCommandAsync(string args)
    {
        var store = _snippetStore;
        if (store == null)
            return new SuccessResult("常用语存储未初始化");

        var cmd = SnippetCommandParser.Parse(args);
        if (cmd.Action == SnippetAction.Unknown)
        {
            var firstToken = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(firstToken))
            {
                var existing = await store.GetAsync(firstToken).ConfigureAwait(false);
                if (existing != null)
                    cmd = new LTAI.Agent.Snippets.SnippetCommand(SnippetAction.Use, firstToken, "", "", null);
            }
        }
        if (cmd.Error != null)
            return new SuccessResult($"[red]{cmd.Error}[/]");

        return cmd.Action switch
        {
            SnippetAction.List => await SnippetListAsync(store).ConfigureAwait(false),
            SnippetAction.Save => await SnippetSaveAsync(store, cmd).ConfigureAwait(false),
            SnippetAction.Use => await SnippetUseAsync(store, cmd).ConfigureAwait(false),
            SnippetAction.Delete => await SnippetDeleteAsync(store, cmd).ConfigureAwait(false),
            SnippetAction.Rename => await SnippetRenameAsync(store, cmd).ConfigureAwait(false),
            SnippetAction.Edit => await SnippetSaveAsync(store,
                new LTAI.Agent.Snippets.SnippetCommand(SnippetAction.Save, cmd.Key, "", cmd.Content, null)).ConfigureAwait(false),
            _ => new SuccessResult($"未知子命令。用法: /snippet list|save|use|edit|rename|delete"),
        };
    }

    private static async Task<CommandResult> SnippetListAsync(SnippetStore store)
    {
        var list = await store.ListAsync().ConfigureAwait(false);
        if (list.Count == 0)
            return new SuccessResult("[yellow]暂无常用语[/]  用法: /snippet save <key> <text>");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Key");
        table.AddColumn("描述");
        table.AddColumn("长度");
        table.AddColumn("使用");
        table.AddColumn("上次使用");

        foreach (var s in list)
        {
            var lastUsed = s.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "[grey]从未[/]";
            var desc = string.IsNullOrEmpty(s.Description) ? "[grey]—[/]" : s.Description.EscapeMarkup();
            table.AddRow(
                $"[cyan]{s.Key.EscapeMarkup()}[/]",
                desc,
                $"{s.Content.Length}",
                s.UseCount > 0 ? $"[green]{s.UseCount}[/]" : "[grey]0[/]",
                lastUsed);
        }

        AnsiConsole.Write(table);
        return new SuccessResult($"[grey]共 {list.Count} 条[/]");
    }

    private static async Task<CommandResult> SnippetSaveAsync(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        try
        {
            await store.SaveAsync(new Snippet
            {
                Key = cmd.Key,
                Content = cmd.Content,
                Description = "",
            }).ConfigureAwait(false);
            return new SuccessResult($"[green]✅ 已保存常用语[/] [cyan]/{cmd.Key}[/] ({cmd.Content.Length} 字符)");
        }
        catch (ArgumentException ex)
        {
            return new SuccessResult($"[red]❌ {ex.Message}[/]");
        }
    }

    private static async Task<CommandResult> SnippetUseAsync(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        var snippet = await store.GetAsync(cmd.Key).ConfigureAwait(false);
        if (snippet == null)
            return new SuccessResult($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]。输入 /snippet list 查看");

        await store.TouchAsync(cmd.Key).ConfigureAwait(false);
        return new SuccessResult(
            $"[green]✅ 已调出常用语[/] [cyan]/{snippet.Key}[/]（{snippet.Content.Length} 字符）。已填入输入框",
            SnippetFill: snippet.Content);
    }

    private static async Task<CommandResult> SnippetDeleteAsync(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        var existing = await store.GetAsync(cmd.Key).ConfigureAwait(false);
        if (existing == null)
            return new SuccessResult($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]");

        var usedHint = existing.UseCount > 0
            ? $" [yellow]（已使用 {existing.UseCount} 次）[/]"
            : "";
        var ok = await store.DeleteAsync(cmd.Key).ConfigureAwait(false);
        return ok
            ? new SuccessResult($"[green]✅ 已删除常用语[/] [cyan]/{cmd.Key}[/]{usedHint}")
            : new SuccessResult($"[red]❌ 删除失败[/]");
    }

    private static async Task<CommandResult> SnippetRenameAsync(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        try
        {
            var ok = await store.RenameAsync(cmd.Key, cmd.NewKey).ConfigureAwait(false);
            return ok
                ? new SuccessResult($"[green]✅ 已重命名[/] [cyan]/{cmd.Key}[/] → [cyan]/{cmd.NewKey}[/]")
                : new SuccessResult($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]");
        }
        catch (InvalidOperationException ex)
        {
            return new SuccessResult($"[red]❌ {ex.Message}[/]");
        }
        catch (ArgumentException ex)
        {
            return new SuccessResult($"[red]❌ {ex.Message}[/]");
        }
    }
}
