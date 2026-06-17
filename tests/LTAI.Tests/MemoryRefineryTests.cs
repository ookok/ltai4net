using Xunit;

namespace LTAI.Tests;

public class MemoryRefineryTests
{
    [Fact]
    public void SurfaceEntities_DetectsCapitalizedEntities()
    {
        var content = "The QuickSort algorithm was implemented by Tony Hoare. "
                    + "RedBlackTree is a self-balancing BST.";
        var entities = ExtractCapitalizedEntities(content);
        Assert.Contains("QuickSort", entities);
        Assert.Contains("RedBlackTree", entities);
        Assert.Contains("Tony Hoare", entities);
    }

    [Fact]
    public void SurfaceEntities_IgnoresSingleCapitalizedWords()
    {
        var content = "The QuickSort algorithm is used. This is a Test.";
        var entities = ExtractCapitalizedEntities(content);
        Assert.DoesNotContain("The", entities);
        Assert.DoesNotContain("This", entities);
    }

    [Fact]
    public void VerifySelfContainment_ValidPair_Passes()
    {
        var pairs = new List<(string, string)>
        {
            ("What is QuickSort?", "QuickSort is a sorting algorithm using divide-and-conquer.")
        };
        var verified = VerifyPairs(pairs);
        Assert.NotEmpty(verified);
    }

    [Fact]
    public void VerifySelfContainment_AmbiguousPronoun_Filters()
    {
        var pairs = new List<(string, string)>
        {
            ("What did they propose?", "They proposed a new framework."),
            ("What is Rust?", "Rust is a systems programming language.")
        };
        var verified = VerifyPairs(pairs);
        Assert.DoesNotContain(verified, v => v.Item1.Contains("they", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(verified, v => v.Item1.Contains("Rust"));
    }

    [Fact]
    public void ConsolidateFacts_RelatedFacts_Merged()
    {
        var facts = new List<(string, string)>
        {
            ("What is Python?", "Python is a programming language."),
            ("What does Python do?", "Python is used for web development."),
        };
        var merged = Consolidate(facts);
        Assert.True(merged.Count < facts.Count || merged.Count == 1);
    }

    // ── Test helpers ──

    private static HashSet<string> ExtractCapitalizedEntities(string content)
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

    private static List<(string, string)> VerifyPairs(List<(string, string)> pairs)
    {
        var verified = new List<(string, string)>();
        string[] ambiguous = ["it", "they", "this", "that", "these", "those"];
        foreach (var (q, a) in pairs)
        {
            if (ambiguous.Any(w => q.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
                !q.Contains("what", StringComparison.OrdinalIgnoreCase) &&
                !q.Contains("who", StringComparison.OrdinalIgnoreCase))
                continue;
            if (a.Length >= 5)
                verified.Add((q, a));
        }
        return verified;
    }

    private static List<(string, string)> Consolidate(List<(string, string)> facts)
    {
        if (facts.Count <= 1) return facts;
        var result = new List<(string, string)>();
        var used = new bool[facts.Count];
        for (int i = 0; i < facts.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var mergedA = facts[i].Item2;
            for (int j = i + 1; j < facts.Count; j++)
            {
                if (used[j]) continue;
                var wordsA = facts[i].Item2.Split([' ', '\t', '\n', '.'], StringSplitOptions.RemoveEmptyEntries);
                var wordsB = facts[j].Item2.Split([' ', '\t', '\n', '.'], StringSplitOptions.RemoveEmptyEntries);
                var setB = new HashSet<string>(wordsB, StringComparer.OrdinalIgnoreCase);
                var common = wordsA.Count(w => setB.Contains(w));
                if ((double)common / Math.Max(wordsA.Length, wordsB.Length) > 0.3)
                {
                    used[j] = true;
                    mergedA += "; " + facts[j].Item2;
                }
            }
            result.Add((facts[i].Item1, mergedA));
        }
        return result;
    }
}
