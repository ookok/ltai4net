// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — L2/L3 Semantic Pyramid export (TencentDB-Agent-Memory)
//  L0 = raw messages, L1 = atom facts (reflection room),
//  L2 = scenario blocks, L3 = persona.
// ═══════════════════════════════════════════════════════════════

using System.Text;
using LTAI.Agent.Delta;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

partial class PalaceStore
{
    /// <summary>
    /// Entity-cooccurrence matrix for grouping L1 facts into L2 scenario blocks.
    /// Two facts belong to the same scenario if they share ≥1 entity.
    /// </summary>
    public async Task<List<ScenarioBlock>> BuildScenarioBlocksAsync(
        string wing, int maxDrawers = 50, double similarityThreshold = 0.7)
    {
        var drawers = await SearchByWingAsync(wing, maxDrawers).ConfigureAwait(false);
        if (drawers.Count == 0) return [];

        var blocks = new List<ScenarioBlock>();
        var used = new HashSet<string>();

        for (int i = 0; i < drawers.Count; i++)
        {
            if (!used.Add(drawers[i].DrawerId)) continue;

            var entities = ExtractEntities(drawers[i].Content);
            var group = new List<PalaceStore.Drawer> { drawers[i] };
            var groupEntities = new HashSet<string>(entities, StringComparer.OrdinalIgnoreCase);

            for (int j = i + 1; j < drawers.Count; j++)
            {
                if (used.Contains(drawers[j].DrawerId)) continue;

                var otherEntities = ExtractEntities(drawers[j].Content);
                var overlap = groupEntities.Overlaps(otherEntities);

                var timeProximity = Math.Abs(drawers[j].CreatedAt - drawers[i].CreatedAt) < 300_000;

                if (overlap && timeProximity)
                {
                    used.Add(drawers[j].DrawerId);
                    group.Add(drawers[j]);
                    foreach (var e in otherEntities) groupEntities.Add(e);
                }
            }

            var combinedContent = string.Join("\n", group.Select(d => d.Content));
            var theme = DeriveScenarioTheme(groupEntities, combinedContent);

            blocks.Add(new ScenarioBlock(
                ScenarioId: $"scenario-{blocks.Count + 1}",
                Theme: theme,
                Drawers: group.AsReadOnly(),
                Entities: groupEntities.ToList().AsReadOnly(),
                CreatedAt: group.Min(d => d.CreatedAt),
                Importance: group.Average(d => d.Importance)));
        }

        return blocks;
    }

    /// <summary>
    /// Extracts L3 persona from L2 scenario blocks.
    /// </summary>
    public async Task<PersonaProfile?> ExtractPersonaAsync(
        string wing, int maxScenarios = 10)
    {
        var blocks = await BuildScenarioBlocksAsync(wing, maxDrawers: 100).ConfigureAwait(false);
        if (blocks.Count == 0) return null;

        var topBlocks = blocks.OrderByDescending(b => b.Importance).Take(maxScenarios).ToList();

        var allEntities = topBlocks.SelectMany(b => b.Entities).Distinct().ToList();
        var expertise = allEntities
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.Key)
            .ToList();

        var themes = topBlocks
            .Select(b => b.Theme)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList();

        var totalDrawers = topBlocks.Sum(b => b.Drawers.Count);
        var avgImportance = topBlocks.Average(b => b.Importance);

        return new PersonaProfile(
            Wing: wing,
            Expertise: expertise.AsReadOnly(),
            Themes: themes.AsReadOnly(),
            ScenarioCount: topBlocks.Count,
            TotalDrawers: totalDrawers,
            AverageImportance: avgImportance,
            LastActive: topBlocks.Max(b => b.CreatedAt));
    }

    /// <summary>
    /// Exports L2 scenario blocks as readable Markdown for white-box debugging.
    /// </summary>
    public async Task<string> ExportL2ToMarkdownAsync(string wing, int maxBlocks = 10)
    {
        var blocks = await BuildScenarioBlocksAsync(wing, maxDrawers: 100).ConfigureAwait(false);
        var top = blocks.OrderByDescending(b => b.Importance).Take(maxBlocks).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# L2 Scenario Blocks — `{wing}`");
        sb.AppendLine();
        sb.AppendLine($"- **Total scenarios**: {blocks.Count}");
        sb.AppendLine($"- **Shown**: {top.Count}");
        sb.AppendLine();

        for (int i = 0; i < top.Count; i++)
        {
            var b = top[i];
            sb.AppendLine($"## Scenario {i + 1}: {b.Theme}");
            sb.AppendLine();
            sb.AppendLine($"- **ID**: `{b.ScenarioId}`");
            sb.AppendLine($"- **Entities**: {string.Join(", ", b.Entities)}");
            sb.AppendLine($"- **Importance**: {b.Importance:F2}");
            sb.AppendLine($"- **Drawers**: {b.Drawers.Count}");
            sb.AppendLine();
            sb.AppendLine("### Facts");
            sb.AppendLine();
            for (int j = 0; j < b.Drawers.Count; j++)
            {
                var d = b.Drawers[j];
                var preview = d.Content.Length > 200 ? d.Content[..200] + "…" : d.Content;
                sb.AppendLine($"{j + 1}. `[{d.DrawerId[..8]}]` {preview}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"*Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        return sb.ToString();
    }

    /// <summary>
    /// Exports L3 persona as readable Markdown for white-box debugging.
    /// </summary>
    public async Task<string> ExportL3ToMarkdownAsync(string wing)
    {
        var persona = await ExtractPersonaAsync(wing).ConfigureAwait(false);
        if (persona == null) return $"# L3 Persona — `{wing}`\n\n*No persona data available.*\n";

        var sb = new StringBuilder();
        sb.AppendLine($"# L3 Persona — `{wing}`");
        sb.AppendLine();
        sb.AppendLine("## Expertise");
        sb.AppendLine();
        foreach (var exp in persona.Expertise)
            sb.AppendLine($"- {exp}");
        sb.AppendLine();
        sb.AppendLine("## Themes");
        sb.AppendLine();
        foreach (var theme in persona.Themes)
            sb.AppendLine($"- {theme}");
        sb.AppendLine();
        sb.AppendLine("## Metrics");
        sb.AppendLine();
        sb.AppendLine($"- **Scenarios analyzed**: {persona.ScenarioCount}");
        sb.AppendLine($"- **Total facts**: {persona.TotalDrawers}");
        sb.AppendLine($"- **Avg importance**: {persona.AverageImportance:F2}");
        sb.AppendLine($"- **Last active**: {DateTimeOffset.FromUnixTimeMilliseconds(persona.LastActive):yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        return sb.ToString();
    }

    /// <summary>
    /// Enhanced persona extraction with code provenance linking (DeltaDB-inspired).
    /// Links each persona expertise to the code it produced via DeltaStore.
    /// </summary>
    public async Task<string> ExportL3WithCodeProvenanceAsync(string wing, CodeProvenanceIndex? codeProvenance = null)
    {
        var persona = await ExtractPersonaAsync(wing).ConfigureAwait(false);
        if (persona == null) return $"# L3 Persona — `{wing}`\n\n*No persona data available.*\n";

        var sb = new StringBuilder();
        sb.AppendLine($"# L3 Persona — `{wing}`");
        sb.AppendLine();
        sb.AppendLine("## Expertise & Code Provenance");
        sb.AppendLine();

        foreach (var exp in persona.Expertise)
        {
            sb.AppendLine($"- **{exp}**");

            if (codeProvenance != null)
            {
                try
                {
                    var provs = await codeProvenance.FindProvenanceForSymbolAsync(exp).ConfigureAwait(false);
                    if (provs.Count > 0)
                    {
                        foreach (var p in provs.Take(3))
                        {
                            sb.AppendLine($"  - conv:{p.ConversationId[..8]} msg:{p.MessageId[..8]} ({p.ToolName})");
                        }
                    }
                }
                catch { /* best-effort */ }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Themes");
        sb.AppendLine();
        foreach (var theme in persona.Themes)
            sb.AppendLine($"- {theme}");
        sb.AppendLine();
        sb.AppendLine("## Metrics");
        sb.AppendLine();
        sb.AppendLine($"- **Scenarios analyzed**: {persona.ScenarioCount}");
        sb.AppendLine($"- **Total facts**: {persona.TotalDrawers}");
        sb.AppendLine($"- **Avg importance**: {persona.AverageImportance:F2}");
        sb.AppendLine($"- **Last active**: {DateTimeOffset.FromUnixTimeMilliseconds(persona.LastActive):yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        return sb.ToString();
    }

    public sealed record ScenarioBlock(
        string ScenarioId,
        string Theme,
        IReadOnlyList<Drawer> Drawers,
        IReadOnlyList<string> Entities,
        long CreatedAt,
        double Importance);

    public sealed record PersonaProfile(
        string Wing,
        IReadOnlyList<string> Expertise,
        IReadOnlyList<string> Themes,
        int ScenarioCount,
        int TotalDrawers,
        double AverageImportance,
        long LastActive);

    /// <summary>Derive a theme label from entity set and content summary.</summary>
    private static string DeriveScenarioTheme(HashSet<string> entities, string combinedContent)
    {
        if (entities.Count > 0)
        {
            var top = entities.Take(3).ToList();
            var theme = string.Join(" / ", top);
            return theme.Length > 80 ? theme[..80] + "…" : theme;
        }
        var preview = combinedContent.Length > 60 ? combinedContent[..60] + "…" : combinedContent;
        return preview.Replace('\n', ' ');
    }
}
