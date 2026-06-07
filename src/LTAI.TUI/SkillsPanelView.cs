using System.Text.Json;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class SkillsPanelView
{
    private readonly string _skillsDir;
    private readonly string? _usageFile;
    private readonly TimeSpan _ttl = TimeSpan.FromDays(30);

    public SkillsPanelView(string skillsDirRoot)
    {
        _skillsDir = skillsDirRoot;
        _usageFile = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "skill_usage.json");
    }

    public void Render()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold]技能管理[/]") { Style = Style.Parse("bold") });
        AnsiConsole.MarkupLine("[dim]Esc: 返回  |  F: 遗忘技能[/]\n");

        if (!Directory.Exists(_skillsDir))
        {
            AnsiConsole.MarkupLine("[red]技能目录不存在[/]");
            return;
        }

        var usage = LoadUsage();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("技能名");
        table.AddColumn("描述");
        table.AddColumn("状态");
        table.AddColumn("上次使用");

        var allMds = Directory.GetFiles(_skillsDir, "SKILL.md", SearchOption.AllDirectories).OrderBy(md => Path.GetFileName(Path.GetDirectoryName(md) ?? ""));
        foreach (var md in allMds)
        {
            var desc = GetDescription(md);
            var name = Path.GetFileName(Path.GetDirectoryName(md)!)!;
            var lastUsed = usage.TryGetValue(name, out var dt) ? dt : (DateTime?)null;
            var expired = lastUsed.HasValue && (DateTime.UtcNow - lastUsed.Value) > _ttl;
            var neverUsed = !lastUsed.HasValue;

            var status = neverUsed ? "[yellow]未使用[/]"
                       : expired   ? "[red]已过期[/]"
                       :             "[green]活跃[/]";
            var timeStr = lastUsed?.ToString("yyyy-MM-dd") ?? "[grey]从未[/]";

            table.AddRow(name, desc.EscapeMarkup(), status, timeStr);
        }

        AnsiConsole.Write(table);

        var forget = AnsiConsole.Confirm("[yellow]要遗忘技能?[/]", false);
        if (forget)
        {
            var name = AnsiConsole.Ask<string>("[yellow]要遗忘的技能名:[/]").Trim();
            if (!string.IsNullOrEmpty(name))
            {
                usage.Remove(name);
                SaveUsage(usage);
                AnsiConsole.MarkupLine($"[green]已遗忘: {name}[/]");
                AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title("[dim]按 Enter 继续[/]").PageSize(3).AddChoices("继续"));
            }
        }
    }

    private static string GetDescription(string skillMd)
    {
        try
        {
            var content = File.ReadAllText(skillMd);
            if (content.StartsWith("---\n") || content.StartsWith("---\r\n"))
            {
                var end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
                if (end > 0)
                {
                    var fm = content[4..end];
                    foreach (var line in fm.Split('\n'))
                    {
                        if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                            return line[12..].Trim().Trim('"').Trim('\'');
                    }
                }
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(skillMd);
    }

    private Dictionary<string, DateTime> LoadUsage()
    {
        if (_usageFile == null || !File.Exists(_usageFile)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_usageFile)) ?? new(); }
        catch { return new(); }
    }

    private void SaveUsage(Dictionary<string, DateTime> usage)
    {
        if (_usageFile == null) return;
        try
        {
            var dir = Path.GetDirectoryName(_usageFile);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(_usageFile, JsonSerializer.Serialize(usage));
        }
        catch { }
    }
}
