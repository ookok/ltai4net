using Xunit;

namespace LTAI.Tests;

public class EntitySurfacingTests
{
    [Fact]
    public void SurfaceEntities_ExtractsMultiWordEntities()
    {
        var content = "The QuickSort algorithm was created by Tony Hoare. "
                    + "RedBlackTree is a self-balancing BST.";
        var entities = ExtractEntities(content);
        Assert.Contains("QuickSort", entities);
        Assert.Contains("Tony Hoare", entities);
    }

    [Fact]
    public void SurfaceEntities_GeneratesForwardAndBackwardQA()
    {
        var content = "The QuickSort algorithm uses divide-and-conquer.";
        var entities = ExtractEntities(content);
        Assert.Contains("QuickSort", entities);

        var pairs = GenerateQAPairs(entities, content);
        // Forward: "What is QuickSort?"
        Assert.Contains(pairs, p => p.Q.Contains("What is QuickSort"));
        // Backward: "What is QuickSort known for?" (reversal curse mitigation)
        Assert.Contains(pairs, p => p.Q.Contains("QuickSort known for"));
    }

    [Fact]
    public void SurfaceEntities_IgnoresShortOrLowerCase()
    {
        var content = "the quick brown fox jumps over the lazy dog. "
                    + "This is a test of the emergency broadcast system.";
        var entities = ExtractEntities(content);
        // Single capitalized words at start of sentence should not be entities
        // "The", "This" are sentence starters, not named entities
        Assert.DoesNotContain("The", entities);
        Assert.DoesNotContain("This", entities);
    }

    [Fact]
    public void GenerateReverseQA_IncludesSourceWingAndRoom()
    {
        var content = "The QuickSort algorithm uses divide-and-conquer.";
        var entities = ExtractEntities(content);
        var pairs = GenerateQAPairsWithContext(entities, content, "code", "algorithms");

        foreach (var (q, a) in pairs)
        {
            Assert.Contains("code/algorithms", a);
        }
    }

    // ── Helper methods matching PalaceStore's entity surfacing logic ──

    private static HashSet<string> ExtractEntities(string content)
    {
        var entities = new HashSet<string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(content, @"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b"))
        {
            var entity = m.Groups[1].Value.Trim();
            if (entity.Length >= 3 && entity.Length <= 60)
                entities.Add(entity);
        }
        return entities;
    }

    private static List<(string Q, string A)> GenerateQAPairs(HashSet<string> entities, string content)
    {
        var pairs = new List<(string, string)>();
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
                        pairs.Add(($"Who or what is {entity}?", $"{entity} — {desc}"));
                        pairs.Add(($"What is {entity} known for?", $"{entity}: {desc}"));
                        break;
                    }
                }
            }
        }
        return pairs;
    }

    private static List<(string Q, string A)> GenerateQAPairsWithContext(
        HashSet<string> entities, string content, string wing, string room)
    {
        var pairs = new List<(string, string)>();
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
}
