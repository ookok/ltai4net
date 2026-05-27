using LTAI.Agent.Skills;
using LTAI.Knowledge.Core;
using LTAI.Models;

namespace LTAI.Agent.MAF;

public sealed class SystemPromptAssembler
{
    private readonly SkillRegistry? _skills;
    private string? _agentsMdContent;
    private DateTime _agentsMdLoadedAt;

    public SystemPromptAssembler(SkillRegistry? skills = null)
    {
        _skills = skills;
    }

    public string Assemble(PromptLayerContext ctx)
    {
        var layers = new List<string>();

        var agentsMd = LoadAgentsMd(ctx.WorkspaceRoot);
        if (!string.IsNullOrEmpty(agentsMd))
            layers.Add(LayerTag("AGENTS.md", ctx.WorkspaceRoot, agentsMd));

        if (!string.IsNullOrEmpty(ctx.ModeHint))
            layers.Add(LayerTag("Mode", null, ctx.ModeHint));

        layers.Add(BuildEnvironmentLayer(ctx));

        if (_skills != null && !ctx.SuppressSkills)
        {
            var skillLayer = BuildSkillLayer(ctx.Domain);
            if (!string.IsNullOrEmpty(skillLayer))
                layers.Add(skillLayer);
        }

        if (!string.IsNullOrEmpty(ctx.TaskInstructions))
            layers.Add(LayerTag("Task", null, ctx.TaskInstructions));

        if (!string.IsNullOrEmpty(ctx.BuildDiagnostics))
            layers.Add(LayerTag("Diagnostics", null, ctx.BuildDiagnostics));

        if (!string.IsNullOrEmpty(ctx.MemoryContext))
            layers.Add(LayerTag("Memory", null, ctx.MemoryContext));

        return string.Join("\n\n", layers);
    }

    private string LoadAgentsMd(string workspaceRoot)
    {
        try
        {
            var path = Path.Combine(workspaceRoot, "AGENTS.md");
            if (!File.Exists(path))
            {
                _agentsMdContent = null;
                return "";
            }

            var lastWrite = File.GetLastWriteTimeUtc(path);
            if (_agentsMdContent != null && _agentsMdLoadedAt >= lastWrite)
                return _agentsMdContent;

            _agentsMdContent = File.ReadAllText(path);
            _agentsMdLoadedAt = DateTime.UtcNow;
            return _agentsMdContent;
        }
        catch
        {
            if (_agentsMdContent != null) return _agentsMdContent;
        }

        return "";
    }

    private static string BuildEnvironmentLayer(PromptLayerContext ctx)
    {
        var lines = new List<string>
        {
            $"Workspace Root: {ctx.WorkspaceRoot}",
            $"Current Directory: {ctx.CurrentDir ?? ctx.WorkspaceRoot}",
            $"Platform: {ctx.Platform ?? Environment.OSVersion.Platform.ToString().ToLowerInvariant()}",
            $"Date: {ctx.Date ?? DateTime.Now.ToString("yyyy-MM-dd")}",
        };

        if (!string.IsNullOrEmpty(ctx.Shell))
            lines.Add($"Shell: {ctx.Shell}");

        if (!string.IsNullOrEmpty(ctx.GitBranch))
            lines.Add($"Git Branch: {ctx.GitBranch}");

        if (ctx.GitClean.HasValue)
            lines.Add($"Git Clean: {(ctx.GitClean.Value ? "yes" : "no (uncommitted changes)")}");

        if (ctx.BuildOk.HasValue)
            lines.Add($"Build Status: {(ctx.BuildOk.Value ? "passing" : "failing")}");

        return LayerTag("Environment", null, string.Join("\n", lines));
    }

    private string BuildSkillLayer(string? domain)
    {
        if (_skills == null) return "";

        try
        {
            var byLayer = new Dictionary<SkillLayer, List<string>>();
            foreach (var skill in _skills.All.Values)
            {
                if (!string.IsNullOrEmpty(domain) && skill.Domain != domain) continue;
                if (!skill.IsActive) continue;

                var desc = string.IsNullOrEmpty(skill.Description)
                    ? skill.Intent
                    : skill.Description;
                var shortDesc = desc.Length > 80 ? desc[..77] + "..." : desc;

                if (!byLayer.TryGetValue(skill.Layer, out var list))
                {
                    list = new List<string>();
                    byLayer[skill.Layer] = list;
                }
                list.Add($"- `{skill.Name}`: {shortDesc}");
            }

            if (byLayer.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var kv in byLayer.OrderBy(kv => (int)kv.Key))
            {
                var layerName = kv.Key switch
                {
                    SkillLayer.L0 => "L0 Atomic",
                    SkillLayer.L1 => "L1 Task",
                    SkillLayer.L2 => "L2 Workflow",
                    SkillLayer.L3 => "L3 Domain",
                    SkillLayer.L4 => "L4 Meta",
                    _ => kv.Key.ToString()
                };
                sb.AppendLine($"### {layerName}");
                foreach (var s in kv.Value)
                    sb.AppendLine(s);
                sb.AppendLine();
            }

            return LayerTag("Skills", null, sb.ToString().TrimEnd());
        }
        catch { return ""; }
    }

    private static string LayerTag(string layerName, string? source, string content)
    {
        var header = string.IsNullOrEmpty(source)
            ? $"[{layerName}]"
            : $"[{layerName} \u2190 {source}]";
        return $"{header}\n{content}";
    }

    public void InvalidateAgentsMdCache()
    {
        _agentsMdContent = null;
    }
}

public sealed class PromptLayerContext
{
    public string WorkspaceRoot { get; init; } = OptionService.Get("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory();
    public string? CurrentDir { get; init; }
    public string? Platform { get; init; }
    public string? Date { get; init; }
    public string? Shell { get; init; }
    public string? GitBranch { get; init; }
    public bool? GitClean { get; init; }
    public bool? BuildOk { get; init; }
    public string? Domain { get; init; }
    public string? ModeHint { get; init; }
    public string? TaskInstructions { get; init; }
    public string? BuildDiagnostics { get; init; }
    public string? MemoryContext { get; init; }
    public bool SuppressSkills { get; init; }
}
