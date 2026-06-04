using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Core.Commands;

public sealed class CommandParser : ICommandParser
{
    private readonly Dictionary<string, string> _nameIndex;

    public CommandParser()
    {
        _nameIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cmd, aliases) in KnownCommands)
        {
            _nameIndex[cmd] = cmd;
            foreach (var alias in aliases)
                _nameIndex[alias] = cmd;
        }
    }

    private static readonly (string cmd, string[] aliases)[] KnownCommands =
    [
        ("help",     ["?", "帮助"]),
        ("new",      ["reset", "clear", "新", "新建", "重置"]),
        ("retry",    ["重试", "重发"]),
        ("compact",  ["压缩", "汇总"]),
        ("model",    []),
        ("models",   ["在线模型", "provider列表"]),
        ("status",   ["状态", "统计"]),
        ("jobs",     ["job", "任务"]),
        ("cost",     ["费用", "花费"]),
        ("config",   ["apikey", "导出", "导入"]),
        ("snippet",  ["snip", "常用语", "常用", "短语"]),
        ("workflow", ["wf", "编排", "工作流"]),
        ("pipe",     ["pipeline", "顺序", "并发"]),
        ("mode",     []),
        ("undo",     ["撤销"]),
        ("ls",       ["dir", "列表"]),
        ("cd",       []),
        ("pwd",      ["目录"]),
        ("approve",  ["yes", "confirm", "批准", "确认"]),
        ("plan",     ["计划状态"]),
        ("lang",     ["语言", "language"]),
        ("skill",    []),
        ("git",      ["g"]),
        ("graph",    ["g", "图"]),
        ("exit",     ["quit", "q", "退出"]),
    ];

    public Command Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new EmptyCommand();

        var trimmed = input.Trim();

        if (!trimmed.StartsWith('/'))
            return new ChatMessageCommand(trimmed);
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmdName = parts[0][1..].ToLowerInvariant();
        if (string.IsNullOrEmpty(cmdName))
            cmdName = "help";
        var args = parts.Length > 1 ? parts[1] : "";

        if (_nameIndex.TryGetValue(cmdName, out var canonical))
            return CreateCommand(canonical, args);

        // Fuzzy match: Levenshtein distance ≤ 3
        var closest = _nameIndex.Keys
            .Select(k => (name: k, dist: Levenshtein(cmdName, k)))
            .Where(x => x.dist <= 3)
            .OrderBy(x => x.dist)
            .FirstOrDefault();

        return new UnknownCommand(cmdName,
            closest.name != null ? _nameIndex[closest.name] : null);
    }

    private static Command CreateCommand(string canonical, string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            return canonical switch
            {
                "help" => new HelpCommand(),
                "exit" => new ExitCommand(),
                "new" => new NewSessionCommand(),
                "retry" => new RetryCommand(),
                "compact" => new CompactCommand(),
                "status" => new StatusCommand(),
                "cost" => new CostCommand(),
                "pwd" => new PwdCommand(),
                "approve" => new ApproveCommand(),
                "plan" => new PlanCommand(),
                "undo" => new UndoCommand(),
                "models" => new ModelsCommand(),
                _ => args is "" && HasArgs(canonical)
                    ? CreateWithArgs(canonical, "")
                    : CreateWithArgs(canonical, args),
            };
        }

        return CreateWithArgs(canonical, args);
    }

    private static Command CreateWithArgs(string canonical, string args) => canonical switch
    {
        "model" => new ModelCommand(args),
        "jobs" => new JobsCommand(args),
        "config" => new ConfigCommand(args),
        "snippet" => new SnippetCommand(args),
        "workflow" => new WorkflowCommand(args),
        "pipe" => new PipeCommand(args),
        "mode" => new ModeCommand(args),
        "ls" => new LsCommand(args),
        "cd" => new CdCommand(args),
        "lang" => new LangCommand(args),
        "skill" => new SkillCommand(args),
        "git" => new GitCommand(args),
        "graph" => new GraphCommand(args),
        _ => new UnknownCommand(canonical),
    };

    private static bool HasArgs(string cmd) => cmd switch
    {
        "model" or "jobs" or "config" or "snippet" or "workflow" or "pipe" or
        "mode" or "ls" or "cd" or "lang" or "skill" or "git" or "graph" => true,
        _ => false,
    };

    private static int Levenshtein(string a, string b)
    {
        var lenA = a.Length;
        var lenB = b.Length;
        var d = new int[lenA + 1, lenB + 1];
        for (int i = 0; i <= lenA; d[i, 0] = i++) { }
        for (int j = 0; j <= lenB; d[0, j] = j++) { }
        for (int i = 1; i <= lenA; i++)
        {
            for (int j = 1; j <= lenB; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[lenA, lenB];
    }
}
