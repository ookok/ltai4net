// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SnippetCommandParser — Parse /snippet <subcmd> [args] inputs
//
//  Subcommands:
//    list                          — show all snippets
//    save <key> <text...>          — create / update
//    use <key>                     — recall (returns content)
//    delete <key>  (alias: del, rm, 删除, 删)
//    rename <old> <new>
//    edit <key>                    — alias for save (semantic hint)
//
//  Recognized verbs are matched case-insensitively. The first
//  token is the subcommand; everything after is subargs. Quoted
//  strings are NOT supported (keep it simple; users can use
//  the multi-line TUI editor for content with spaces).
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Snippets;

public enum SnippetAction
{
    Unknown,
    List,
    Save,
    Use,
    Delete,
    Rename,
    Edit,
}

public sealed record SnippetCommand(
    SnippetAction Action,
    string Key,        // primary identifier (for Save/Use/Delete/Edit/Rename's old name)
    string NewKey,     // only for Rename
    string Content,    // for Save/Edit
    string? Error      // populated when Action == Unknown
);

public static class SnippetCommandParser
{
    public static SnippetCommand Parse(string input)
    {
        var raw = (input ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
            return new SnippetCommand(SnippetAction.Unknown, "", "", "",
                "用法: /snippet list|save <key> <text>|use <key>|delete <key>|rename <old> <new>|edit <key>");

        // First token is the subcommand
        var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : "";

        return verb switch
        {
            "list" or "ls" or "列表" or "列" =>
                new SnippetCommand(SnippetAction.List, "", "", "", null),

            "save" or "add" or "保存" or "存" =>
                ParseSave(rest),

            "use" or "调用" or "用" =>
                ParseUse(rest),

            "delete" or "del" or "rm" or "remove" or "删除" or "删" =>
                ParseDelete(rest),

            "rename" or "mv" or "重命名" =>
                ParseRename(rest),

            "edit" or "编辑" or "改" =>
                ParseEdit(rest),

            _ =>
                new SnippetCommand(SnippetAction.Unknown, "", "", "",
                    $"未知子命令 '/{verb}'。用法: /snippet list|save|use|delete|rename|edit"),
        };
    }

    private static SnippetCommand ParseSave(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return new SnippetCommand(SnippetAction.Save, "", "", "",
                "用法: /snippet save <key> <text...>");

        var sp = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length < 2)
            return new SnippetCommand(SnippetAction.Save, sp[0], "", "",
                "用法: /snippet save <key> <text...>（缺少文本内容）");

        return new SnippetCommand(SnippetAction.Save, sp[0], "", sp[1], null);
    }

    private static SnippetCommand ParseUse(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return new SnippetCommand(SnippetAction.Use, "", "", "",
                "用法: /snippet use <key>");
        return new SnippetCommand(SnippetAction.Use, rest.Split(' ')[0], "", "", null);
    }

    private static SnippetCommand ParseDelete(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return new SnippetCommand(SnippetAction.Delete, "", "", "",
                "用法: /snippet delete <key>");
        return new SnippetCommand(SnippetAction.Delete, rest.Split(' ')[0], "", "", null);
    }

    private static SnippetCommand ParseRename(string rest)
    {
        var sp = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length < 2)
            return new SnippetCommand(SnippetAction.Rename, "", "", "",
                "用法: /snippet rename <old> <new>");
        return new SnippetCommand(SnippetAction.Rename, sp[0], sp[1], "", null);
    }

    private static SnippetCommand ParseEdit(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return new SnippetCommand(SnippetAction.Edit, "", "", "",
                "用法: /snippet edit <key> <text...>");
        var sp = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length < 2)
            return new SnippetCommand(SnippetAction.Edit, sp[0], "", "",
                "用法: /snippet edit <key> <text...>（缺少文本内容）");
        return new SnippetCommand(SnippetAction.Edit, sp[0], "", sp[1], null);
    }
}
