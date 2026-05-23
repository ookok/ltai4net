namespace LTAI.Agent.Agents;

public sealed record ExplorationRound
{
    public int Round { get; init; }
    public string Action { get; init; } = "";
    public string Finding { get; init; } = "";
    public List<string> FilesExamined { get; init; } = new();
    public List<string> IssuesFound { get; init; } = new();
    public double Confidence { get; init; }
}

public sealed class ExplorationResult
{
    public List<ExplorationRound> Rounds { get; init; } = new();
    public int TotalFilesExamined => Rounds.Sum(r => r.FilesExamined.Count);
    public int TotalIssuesFound => Rounds.Sum(r => r.IssuesFound.Count);
    public double AverageConfidence => Rounds.Count > 0 ? Rounds.Average(r => r.Confidence) : 0;
}

public sealed class CodeExplorationStrategy
{
    private readonly int _maxRounds;
    private readonly HashSet<string> _examinedFiles = new();

    public CodeExplorationStrategy(int maxRounds = 3)
    {
        _maxRounds = maxRounds;
    }

    public string BuildExplorationPrompt(string task, List<string>? targetFiles = null)
    {
        _examinedFiles.Clear();
        if (targetFiles != null)
            foreach (var f in targetFiles) _examinedFiles.Add(f);

        return $"""
            Multi-round code exploration strategy:
            Round 1 (SCOPE): Identify the relevant files and modules for "{task}".
            Round 2 (ANALYZE): Examine each file's structure, dependencies, and potential issues.
            Round 3 (SYNTHESIZE): Cross-reference findings and produce the final analysis.

            For each round, list:
            - Files examined
            - Key findings
            - Confidence in findings (0.0-1.0)
            - Issues discovered

            Do NOT repeat analysis of already-examined files.
            Max rounds: {_maxRounds}
            """;
    }

    public string BuildNextRoundPrompt(string previousFindings, int roundNumber, HashSet<string> examinedFiles)
    {
        return $"""
            Round {roundNumber}/{_maxRounds}.
            Previously examined files: {string.Join(", ", examinedFiles)}.
            Previous findings: {previousFindings}

            Now explore DEEPER — look at files NOT yet examined.
            Focus on:
            - Cross-file dependencies and imports
            - Error handling patterns across modules
            - Test coverage gaps
            - Design patterns used or missing

            New files to examine (suggest 2-5, not already explored):
            """;
    }

    public bool ShouldContinueExploration(int round, double lastConfidence, int newFilesFound)
    {
        if (round >= _maxRounds) return false;
        if (lastConfidence > 0.9) return false;
        if (round >= 2 && newFilesFound == 0) return false;
        return true;
    }

    public ExplorationResult ParseExplorationOutput(string fullResponse)
    {
        var result = new ExplorationResult();
        var rounds = fullResponse.Split("Round", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var roundText in rounds)
        {
            var r = new ExplorationRound();
            var lines = roundText.Split('\n');

            if (int.TryParse(lines[0].Split(':')[0].Trim(), out var roundNum))
                r = r with { Round = roundNum };

            foreach (var line in lines)
            {
                if (line.Contains("Files examined", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("examined", StringComparison.OrdinalIgnoreCase))
                {
                    var after = line[(line.IndexOf(':') + 1)..];
                    r.FilesExamined.AddRange(after.Split(',', StringSplitOptions.TrimEntries)
                        .Where(f => f.Length > 1));
                }
                if (line.Contains("Issue", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("issue", StringComparison.OrdinalIgnoreCase))
                {
                    var after = line[(line.IndexOf(':') + 1)..];
                    if (after.Length > 3) r.IssuesFound.Add(after.Trim());
                }
                if (line.Contains("Confidence", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"([\d.]+)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out var conf))
                        r = r with { Confidence = conf };
                }
            }

            if (r.FilesExamined.Count > 0 || r.IssuesFound.Count > 0)
                result.Rounds.Add(r);
        }

        return result;
    }
}
