// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — entity extraction & reverse-QA surfacing (MeMo)
// ═══════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;

namespace LTAI.Agent.Memory;

partial class PalaceStore
{
    /// <summary>Extract entity surfacing QA pairs from content (MeMo §4.1 Step 4).</summary>
    private static List<(string Question, string Answer)> SurfaceEntitiesFromContent(
        string content, string wing, string room)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(content)) return pairs;

        var entities = new HashSet<string>();
        foreach (Match m in EntityExtractPattern().Matches(content))
        {
            var entity = m.Groups[1].Value.Trim();
            if (entity.Length >= 3 && entity.Length <= 60)
                entities.Add(entity);
        }

        foreach (var entity in entities.Take(3))
        {
            var sentences = content.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var sentence in sentences)
            {
                if (sentence.Contains(entity, StringComparison.OrdinalIgnoreCase))
                {
                    var desc = sentence.Trim();
                    if (desc.Length > entity.Length + 5)
                    {
                        if (desc.Length > 150) desc = desc[..147] + "...";
                        pairs.Add(($"Who or what is {entity}?",
                                   $"In {wing}/{room}: {entity} — {desc}"));
                        pairs.Add(($"What is {entity} known for?",
                                   $"{entity} relates to {wing}/{room}: {desc}"));
                        break;
                    }
                }
            }
        }

        return pairs;
    }

    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled, 500)]
    private static partial Regex EntityExtractPattern();

    /// <summary>Extract capitalized entities from content for scenario grouping.</summary>
    private static List<string> ExtractEntities(string content)
    {
        var entities = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return entities;
        foreach (Match m in EntityExtractPattern().Matches(content))
        {
            var entity = m.Groups[1].Value.Trim();
            if (entity.Length >= 3 && entity.Length <= 60)
                entities.Add(entity);
        }
        return entities;
    }
}
