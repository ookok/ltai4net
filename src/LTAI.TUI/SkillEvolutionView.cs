using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.Agent.Skills;
using LTAI.Models;

namespace LTAI.TUI;

public sealed class SkillEvolutionView
{
    private readonly SkillRegistry? _registry;
    
    public SkillEvolutionView(SkillRegistry? registry = null)
    {
        _registry = registry;
    }
    
    public IRenderable Render()
    {
        if (_registry == null)
            return new Markup("[grey]Skill registry not available.[/]");

        var skills = _registry.All.Values.OrderBy(s => s.Layer).ThenByDescending(s => s.Evolution.SuccessRate).ToList();
        
        var panel = new Panel(BuildTree(skills));
        panel.Header = new PanelHeader($"[yellow]Skill Evolution ({skills.Count} skills)[/]");
        panel.Border = BoxBorder.Rounded;
        return panel;
    }
    
    private IRenderable BuildTree(List<Skill> skills)
    {
        var tree = new Tree("[bold yellow]Skill Tree[/]");
        
        var byLayer = skills.GroupBy(s => s.Layer).OrderBy(g => g.Key);
        
        foreach (var group in byLayer)
        {
            var layerColor = group.Key switch
            {
                SkillLayer.L0 => "grey",
                SkillLayer.L1 => "green",
                SkillLayer.L2 => "blue",
                SkillLayer.L3 => "yellow",
                SkillLayer.L4 => "magenta",
                _ => "white"
            };
            
            var layerNode = tree.AddNode($"[{layerColor}]L{group.Key:D}[/] ([white]{group.Count()}[/])");
            
            foreach (var skill in group.Take(10))
            {
                var rate = skill.Evolution.SuccessRate;
                var rateBar = rate >= 0.9f ? "[green]\u2588\u2588\u2588\u2588[/]" :
                              rate >= 0.7f ? "[yellow]\u2588\u2588\u2588\u2588[/]" :
                              rate >= 0.5f ? "[grey]\u2588\u2588\u2588\u2588[/]" : "[red]\u2588\u2588[/]";
                
                var uses = skill.Evolution.TotalUses;
                var active = skill.IsActive ? "[green]\u25CF[/]" : "[red]\u25CB[/]";
                var reliable = skill.IsReliable ? "[green]\u2713[/]" : "";
                
                var skillNode = layerNode.AddNode(
                    $"{active} [white]{skill.Name}[/] {rateBar} {rate:P0} | [dim]{uses} uses[/] {reliable}");
                
                if (skill.Requires.Count > 0)
                    skillNode.AddNode($"[dim]requires: {string.Join(", ", skill.Requires.Take(3))}[/]");
            }
            
            if (group.Count() > 10)
                layerNode.AddNode($"[dim]... and {group.Count() - 10} more[/]");
        }
        
        var stats = _registry!.GetStats();
        tree.AddNode($"[dim]Active: {stats["active"]} | Reliable: {stats["reliable"]} | Avg uses: {skills.Average(s => s.Evolution.TotalUses):F0}[/]");
        
        return tree;
    }
}
