using System.Text;

namespace LTAI.TUI;

public static class DiffEngine
{
    public static DiffResult Compute(string original, string modified)
    {
        var origLines = original.Split('\n');
        var modLines = modified.Split('\n');
        var edits = ComputeEdits(origLines, modLines);
        return new DiffResult
        {
            OriginalLines = origLines,
            ModifiedLines = modLines,
            Edits = edits,
            AddedCount = edits.Count(e => e.Type == DiffType.Added),
            RemovedCount = edits.Count(e => e.Type == DiffType.Removed),
            ChangedCount = edits.Count(e => e.Type == DiffType.Changed)
        };
    }

    private static List<DiffEdit> ComputeEdits(string[] a, string[] b)
    {
        var lcs = LongestCommonSubsequence(a, b);
        var edits = new List<DiffEdit>();

        int i = 0, j = 0;
        foreach (var (aIdx, bIdx) in lcs)
        {
            while (i < aIdx)
            {
                edits.Add(new DiffEdit { Type = DiffType.Removed, OldLine = i, OldText = a[i] });
                i++;
            }
            while (j < bIdx)
            {
                edits.Add(new DiffEdit { Type = DiffType.Added, NewLine = j, NewText = b[j] });
                j++;
            }

            if (i < a.Length && j < b.Length && a[i] != b[j])
                edits.Add(new DiffEdit { Type = DiffType.Changed, OldLine = i, OldText = a[i], NewLine = j, NewText = b[j] });
            else if (i < a.Length && j < b.Length)
                edits.Add(new DiffEdit { Type = DiffType.Unchanged, OldLine = i, OldText = a[i], NewLine = j });

            i++;
            j++;
        }

        while (i < a.Length)
        {
            edits.Add(new DiffEdit { Type = DiffType.Removed, OldLine = i, OldText = a[i] });
            i++;
        }
        while (j < b.Length)
        {
            edits.Add(new DiffEdit { Type = DiffType.Added, NewLine = j, NewText = b[j] });
            j++;
        }

        return edits;
    }

    private static List<(int aIdx, int bIdx)> LongestCommonSubsequence(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (var i = 1; i <= m; i++)
            for (var j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var result = new List<(int, int)>();
        int x = m, y = n;
        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1])
            {
                result.Add((x - 1, y - 1));
                x--; y--;
            }
            else if (dp[x - 1, y] > dp[x, y - 1])
                x--;
            else
                y--;
        }
        result.Reverse();
        return result;
    }

    public static string RenderUnifiedDiff(DiffResult diff, int context = 3)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[bold]Diff:[/] [red]-{diff.RemovedCount}[/] [green]+{diff.AddedCount}[/] [yellow]~{diff.ChangedCount}[/] lines changed");
        sb.AppendLine();

        var edits = diff.Edits;
        for (var e = 0; e < edits.Count; e++)
        {
            var edit = edits[e];

            switch (edit.Type)
            {
                case DiffType.Unchanged:
                    if (IsNearChange(edits, e, context))
                        sb.AppendLine($"  [grey]{edit.OldLine + 1,4}[/] {EscapeD(edit.OldText)}");
                    break;
                case DiffType.Added:
                    sb.AppendLine($"[green]+{edit.NewLine + 1,4}[/] [green]{EscapeD(edit.NewText)}[/]");
                    break;
                case DiffType.Removed:
                    sb.AppendLine($"[red]-{edit.OldLine + 1,4}[/] [red]{EscapeD(edit.OldText)}[/]");
                    break;
                case DiffType.Changed:
                    sb.AppendLine($"[red]-{edit.OldLine + 1,4}[/] [red]{EscapeD(edit.OldText)}[/]");
                    sb.AppendLine($"[green]+{edit.NewLine + 1,4}[/] [green]{EscapeD(edit.NewText)}[/]");
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsNearChange(List<DiffEdit> edits, int index, int context)
    {
        for (var i = Math.Max(0, index - context); i <= Math.Min(edits.Count - 1, index + context); i++)
        {
            if (edits[i].Type != DiffType.Unchanged)
                return true;
        }
        return false;
    }

    private static string EscapeD(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");
}

public sealed class DiffResult
{
    public string[] OriginalLines { get; init; } = Array.Empty<string>();
    public string[] ModifiedLines { get; init; } = Array.Empty<string>();
    public List<DiffEdit> Edits { get; init; } = new();
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ChangedCount { get; set; }
}

public sealed class DiffEdit
{
    public DiffType Type { get; init; }
    public int OldLine { get; init; }
    public string OldText { get; init; } = "";
    public int NewLine { get; init; }
    public string NewText { get; init; } = "";
}

public enum DiffType { Unchanged, Added, Removed, Changed }
