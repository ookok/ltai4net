namespace LTAI.Agent.Tools.Review;

/// <summary>
/// Post-review quality reflection. Checks coverage, specificity, and
/// produces a quality rating. Inspired by OCR's reflection module.
/// </summary>
public sealed class ReviewReflector
{
    /// <summary>
    /// Reflect on review quality: check coverage, severity distribution,
    /// and comment specificity.
    /// </summary>
    public ReflectionResult Reflect(
        List<ReviewComment> comments,
        List<DiffFileInfo> changedFiles)
    {
        var commentedFiles = comments
            .Where(c => !string.IsNullOrEmpty(c.FilePath))
            .Select(c => c.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet();

        var changedPaths = changedFiles.Select(f => f.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missedFiles = changedFiles
            .Where(f => !commentedFiles.Contains(f.FilePath) &&
                        !f.FilePath.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) &&
                        !f.FilePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FilePath)
            .ToList();

        var p0Count = comments.Count(c => c.Severity == "P0");
        var p1Count = comments.Count(c => c.Severity == "P1");
        var p2Count = comments.Count(c => c.Severity == "P2");

        var coveredFiles = changedFiles.Count(f => commentedFiles.Contains(f.FilePath));
        var totalReviewable = changedFiles.Count(f =>
            !f.FilePath.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) &&
            !f.FilePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase));

        // Assess comment specificity — comments with file:line are specific
        var specificComments = comments.Count(c =>
            !string.IsNullOrEmpty(c.FilePath) && c.LineNumber > 0);
        var specificityRatio = comments.Count > 0
            ? (double)specificComments / comments.Count
            : 0;

        // Quality rating
        var qualityRating = CalculateQualityRating(
            coveredFiles, totalReviewable, p0Count, specificityRatio);

        return new ReflectionResult(
            TotalComments: comments.Count,
            CoveredFiles: coveredFiles,
            MissedFiles: missedFiles.Count,
            MissedFilePaths: missedFiles,
            P0Count: p0Count,
            P1Count: p1Count,
            P2Count: p2Count,
            QualityRating: qualityRating);
    }

    /// <summary>Generate a human-readable reflection report.</summary>
    public string ToReport(ReflectionResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Review Quality Reflection");
        sb.AppendLine();
        sb.AppendLine($"**Quality Rating:** {result.QualityRating}");
        sb.AppendLine($"**Total Comments:** {result.TotalComments} (P0: {result.P0Count}, P1: {result.P1Count}, P2: {result.P2Count})");
        sb.AppendLine($"**Coverage:** {result.CoveredFiles} files reviewed, {result.MissedFiles} files missed");
        sb.AppendLine();

        if (result.MissedFilePaths.Count > 0)
        {
            sb.AppendLine("### 📋 Files Not Reviewed");
            foreach (var file in result.MissedFilePaths)
                sb.AppendLine($"  - {file}");
            sb.AppendLine();
        }

        if (result.P0Count > 0)
        {
            sb.AppendLine("### 🔴 Critical Issues (P0)");
            sb.AppendLine($"  {result.P0Count} critical issues found — must be addressed before merge");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string CalculateQualityRating(
        int coveredFiles, int totalFiles, int criticalCount, double specificityRatio)
    {
        if (totalFiles == 0) return "excellent";

        var coverageRatio = (double)coveredFiles / totalFiles;

        if (coverageRatio >= 0.8 && specificityRatio >= 0.7 && criticalCount <= 1)
            return "excellent";
        if (coverageRatio >= 0.6 && specificityRatio >= 0.5 && criticalCount <= 3)
            return "good";
        if (coverageRatio >= 0.4 || specificityRatio >= 0.3)
            return "fair";

        return "poor";
    }
}
