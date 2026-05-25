using LTAI.Agent.Skills;
using LTAI.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LTAI.Cli.Commands;

public sealed class SkillListCommand : AsyncCommand<SkillListCommand.Settings>
{
    private readonly SkillRegistry _registry;

    public SkillListCommand(SkillRegistry registry)
    {
        _registry = registry;
    }

    public sealed class Settings : CommandSettings { }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var all = _registry.All;

        var table = new Table()
            .Title("[bold cyan]Skills[/]")
            .AddColumns("Layer", "Name", "Domain", "Conf", "Uses", "Rate");

        foreach (var skill in all.Values.OrderBy(s => s.Layer).ThenBy(s => s.Domain))
        {
            var layer = skill.Layer switch
            {
                SkillLayer.L0 => "[grey]L0[/]",
                SkillLayer.L1 => "[blue]L1[/]",
                SkillLayer.L2 => "[yellow]L2[/]",
                SkillLayer.L3 => "[green]L3[/]",
                SkillLayer.L4 => "[magenta]L4[/]",
                _ => "?"
            };

            var active = skill.IsActive ? "" : " [dim](inactive)[/]";
            table.AddRow(
                layer,
                $"{skill.Name}{active}",
                skill.Domain,
                $"{skill.Confidence:F2}",
                skill.Evolution.TotalUses.ToString(),
                $"{skill.Evolution.SuccessRate:P0}"
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{all.Count} skills loaded[/]");
        return await Task.FromResult(0);
    }
}

public sealed class SkillInstallCommand : AsyncCommand<SkillInstallCommand.Settings>
{
    private readonly SkillInstaller _installer;

    public SkillInstallCommand(SkillInstaller installer)
    {
        _installer = installer;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<source>")]
        public string Source { get; init; } = "";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        Skill? skill = null;

        if (settings.Source.StartsWith("http"))
            skill = await _installer.InstallFromUrlAsync(settings.Source);
        else if (settings.Source.Contains('/') && !settings.Source.StartsWith("github.com/"))
            skill = await _installer.InstallFromLocalAsync(settings.Source);
        else
            skill = await _installer.InstallFromGitHubAsync(settings.Source);

        if (skill != null)
            AnsiConsole.MarkupLine($"[green]Installed:[/] {skill.Name} ({skill.LayerDir}/{skill.Name}.md)");
        else
            AnsiConsole.MarkupLine("[red]Install failed[/]");

        return skill != null ? 0 : -1;
    }
}

public sealed class SkillExtractCommand : AsyncCommand<SkillExtractCommand.Settings>
{
    private readonly SkillExtractor _extractor;

    public SkillExtractCommand(SkillExtractor extractor)
    {
        _extractor = extractor;
    }

    public sealed class Settings : CommandSettings { }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var extracted = await _extractor.ExtractAllReadyAsync();
        if (extracted.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No skills ready for extraction (need >=3 successful patterns)[/]");
            return 0;
        }

        foreach (var skill in extracted)
            AnsiConsole.MarkupLine($"[green]Extracted:[/] {skill.Name} → {skill.LayerDir}/{skill.Name}.md");

        return 0;
    }
}

public sealed class SkillStatsCommand : AsyncCommand<SkillStatsCommand.Settings>
{
    private readonly SkillRegistry _registry;

    public SkillStatsCommand(SkillRegistry registry)
    {
        _registry = registry;
    }

    public sealed class Settings : CommandSettings { }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var stats = _registry.GetStats();
        var byLayer = stats["by_layer"] as Dictionary<string, int>;

        var panel = new Panel(
            $"""
            [cyan]Total Skills:[/] {stats["total_skills"]}
            [cyan]Domains:[/] {stats["domains"]}
            [cyan]Active:[/] {stats["active"]}
            [cyan]Reliable (>=70%):[/] {stats["reliable"]}

            [yellow]By Layer:[/]
              L0 Atomic:  {byLayer?["L0"] ?? 0}
              L1 Task:    {byLayer?["L1"] ?? 0}
              L2 Workflow:{byLayer?["L2"] ?? 0}
              L3 Domain:  {byLayer?["L3"] ?? 0}
              L4 Meta:    {byLayer?["L4"] ?? 0}
            """)
        {
            Header = new PanelHeader("[bold]Skill Statistics[/]")
        };

        AnsiConsole.Write(panel);
        return await Task.FromResult(0);
    }
}
