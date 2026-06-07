using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools.Review;

public sealed class ReviewRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = ""; // correctness, security, performance, maintainability, style
    public string Severity { get; set; } = "warning"; // error, warning, info
    public string? FilePattern { get; set; } // glob: "**/*.cs"
    public string? Pattern { get; set; } // regex to find
    public string? NotPattern { get; set; } // negative regex
    public string? MessageTemplate { get; set; } // {0}=file, {1}=match
    public string? Language { get; set; }
}

public sealed record ReviewRuleMatch(
    string RuleId,
    string RuleName,
    string Category,
    string Severity,
    string FilePath,
    int LineNumber,
    string MatchedText,
    string Message);

public sealed record ReviewComment(
    string FilePath,
    int LineNumber,
    int LineEnd,
    string Severity, // P0/P1/P2
    string Category,
    string Title,
    string Body,
    string? SuggestedFix = null);

public sealed record ReviewReport(
    List<DiffFileInfo> ChangedFiles,
    List<FileGroup> FileGroups,
    List<ReviewRuleMatch> RuleMatches,
    List<ReviewComment> Comments,
    ReflectionResult? Reflection = null);

public sealed record DiffFileInfo(
    string FilePath,
    string Status, // added, modified, deleted, renamed
    string? OldPath = null,
    int AddedLines = 0,
    int DeletedLines = 0);

public sealed record FileGroup(
    string GroupId,
    string GroupName,
    string GroupType, // interface-impl, test-source, locale-resource, code-behind, related, standalone
    List<DiffFileInfo> Files);

public sealed record ReflectionResult(
    int TotalComments,
    int CoveredFiles,
    int MissedFiles,
    List<string> MissedFilePaths,
    int P0Count,
    int P1Count,
    int P2Count,
    string QualityRating); // excellent, good, fair, poor

public sealed record RepairedComment(
    ReviewComment Original,
    ReviewComment? Repaired,
    bool WasRepaired,
    string RepairNote);
