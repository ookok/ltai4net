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

    public CommandResult Execute(Command command) => command switch
    {
        LTAI.Core.Commands.SnippetCommand sc => HandleSnippetCommand(sc.Args),
        _ => new SuccessResult("ok"),
    };

    private CommandResult HandleSnippetCommand(string args)
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
                var existing = store.GetAsync(firstToken).GetAwaiter().GetResult();
                if (existing != null)
                    cmd = new LTAI.Agent.Snippets.SnippetCommand(SnippetAction.Use, firstToken, "", "", null);
            }
        }
        if (cmd.Error != null)
            return new SuccessResult($"[red]{cmd.Error}[/]");

        return cmd.Action switch
        {
            SnippetAction.List => SnippetList(store),
            SnippetAction.Save => SnippetSave(store, cmd),
            SnippetAction.Use => SnippetUse(store, cmd),
            SnippetAction.Delete => SnippetDelete(store, cmd),
            SnippetAction.Rename => SnippetRename(store, cmd),
            SnippetAction.Edit => SnippetSave(store,
                new LTAI.Agent.Snippets.SnippetCommand(SnippetAction.Save, cmd.Key, "", cmd.Content, null)),
            _ => new SuccessResult($"未知子命令。用法: /snippet list|save|use|edit|rename|delete"),
        };
    }

    private static CommandResult SnippetList(SnippetStore store)
    {
        var list = store.ListAsync().GetAwaiter().GetResult();
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

    private static CommandResult SnippetSave(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        try
        {
            store.SaveAsync(new Snippet
            {
                Key = cmd.Key,
                Content = cmd.Content,
                Description = "",
            }).GetAwaiter().GetResult();
            return new SuccessResult($"[green]✅ 已保存常用语[/] [cyan]/{cmd.Key}[/] ({cmd.Content.Length} 字符)");
        }
        catch (ArgumentException ex)
        {
            return new SuccessResult($"[red]❌ {ex.Message}[/]");
        }
    }

    private static CommandResult SnippetUse(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        var snippet = store.GetAsync(cmd.Key).GetAwaiter().GetResult();
        if (snippet == null)
            return new SuccessResult($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]。输入 /snippet list 查看");

        store.TouchAsync(cmd.Key).GetAwaiter().GetResult();
        return new SuccessResult(
            $"[green]✅ 已调出常用语[/] [cyan]/{snippet.Key}[/]（{snippet.Content.Length} 字符）。已填入输入框",
            SnippetFill: snippet.Content);
    }

    private static CommandResult SnippetDelete(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        var existing = store.GetAsync(cmd.Key).GetAwaiter().GetResult();
        if (existing == null)
            return new SuccessResult($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]");

        var usedHint = existing.UseCount > 0
            ? $" [yellow]（已使用 {existing.UseCount} 次）[/]"
            : "";
        var ok = store.DeleteAsync(cmd.Key).GetAwaiter().GetResult();
        return ok
            ? new SuccessResult($"[green]✅ 已删除常用语[/] [cyan]/{cmd.Key}[/]{usedHint}")
            : new SuccessResult($"[red]❌ 删除失败[/]");
    }

    private static CommandResult SnippetRename(SnippetStore store, LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        try
        {
            var ok = store.RenameAsync(cmd.Key, cmd.NewKey).GetAwaiter().GetResult();
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
