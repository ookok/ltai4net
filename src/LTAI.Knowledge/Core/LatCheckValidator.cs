using System.Collections.Concurrent;

namespace LTAI.Knowledge.Core;

public sealed record ValidationSummary
{
    public bool AllPassed { get; set; }
    public int TotalChecks { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public List<string> FailureDetails { get; set; } = new();
    public Dictionary<string, int> FailureByCategory { get; set; } = new();
    public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record FileValidationReport
{
    public string FilePath { get; set; } = "";
    public bool IsValid { get; set; }
    public List<BrokenLink> BrokenLinks { get; set; } = new();
    public int WikiLinks { get; set; }
    public int CodeRefs { get; set; }
    public int Sections { get; set; }
}

public sealed class LatCheckValidator
{
    private readonly MarkdownKnowledgeGraph _kg;
    private readonly CodeLinkTracker? _tracker;
    private readonly ConcurrentDictionary<string, FileValidationReport> _reports = new();
    private readonly ConcurrentQueue<string> _recentFailures = new();

    public LatCheckValidator(MarkdownKnowledgeGraph kg, CodeLinkTracker? tracker = null)
    {
        _kg = kg;
        _tracker = tracker;
    }

    public LatCheckResult ValidateAll(bool includeCodeScan = true)
    {
        var result = _kg.Check(scanCodeRefs: includeCodeScan);

        if (_tracker != null && includeCodeScan)
        {
            var allLinks = _tracker.GetAllLinks();
            foreach (var link in allLinks)
            {
                result.CodeRefsChecked++;

                var section = _kg.GetSection(link.TargetSectionId);
                if (section == null)
                {
                    var sections = _kg.Locate(link.TargetSectionId, fuzzy: true);
                    if (sections.Count == 0)
                    {
                        result.Errors.Add(new BrokenLink(
                            link.SourceFilePath,
                            link.TargetSectionId,
                            BrokenLinkType.CodeRef,
                            $"Code ref in {link.SourceFilePath}:{link.LineNumber} references nonexistent section"));
                    }
                }
            }

            ValidateTestSpecRefs(result);
        }

        result.AllValid = result.Errors.Count == 0;

        var report = new FileValidationReport
        {
            FilePath = "all",
            IsValid = result.AllValid,
            BrokenLinks = result.Errors,
            WikiLinks = result.WikiLinksChecked,
            CodeRefs = result.CodeRefsChecked,
            Sections = result.SectionsScanned
        };
        _reports["all"] = report;

        foreach (var err in result.Errors)
        {
            var category = err.Type.ToString();
            if (err.Message.Contains("test spec") || err.Message.Contains("code mention"))
                category = "TestSpec";

            if (!report.BrokenLinks.Any(b => b.Message == err.Message))
            {
                _recentFailures.Enqueue($"[{category}] {err.Message}");
                while (_recentFailures.Count > 100)
                    _recentFailures.TryDequeue(out _);
            }
        }

        return result;
    }

    public bool QuickCheck()
    {
        var result = _kg.Check(scanCodeRefs: false);
        return result.AllValid;
    }

    private void ValidateTestSpecRefs(LatCheckResult result)
    {
        if (_tracker == null) return;

        var files = _kg.GetAllFiles();
        foreach (var file in files)
        {
            if (!file.Frontmatter.TryGetValue("require-code-mention", out var rcm) || rcm != "true")
                continue;

            foreach (var section in file.Sections)
            {
                if (section.SubHeading == null) continue;

                var refs = _tracker.FindSectionCodeRefs(section.FullId);
                if (refs.Count == 0)
                {
                    refs = _tracker.FindSectionCodeRefs(
                        $"{section.FilePath}#{section.Heading}#{section.SubHeading}");

                    if (refs.Count == 0)
                    {
                        refs = _tracker.FindSectionCodeRefs(
                            $"{Path.GetFileNameWithoutExtension(section.FilePath)}#{section.SubHeading}");
                    }
                }

                if (refs.Count == 0)
                {
                    var err = result.Errors.FirstOrDefault(e =>
                        e.SourceSection == section.FullId && e.Type == BrokenLinkType.CodeMention);

                    if (err == null)
                    {
                        result.Errors.Add(new BrokenLink(
                            section.FullId, section.FullId,
                            BrokenLinkType.CodeMention,
                            $"Test spec '{section.FullId}' has require-code-mention but no code reference found"));
                    }
                }
                else
                {
                    result.Errors.RemoveAll(e =>
                        e.SourceSection == section.FullId && e.Type == BrokenLinkType.CodeMention);
                }

                result.TestSpecsEnforced++;
            }
        }
    }

    public ValidationSummary GetSummary()
    {
        var summary = new ValidationSummary();
        var recent = _recentFailures.ToList();
        summary.TotalChecks = recent.Count;
        summary.Failed = recent.Count;
        summary.Passed = 0;
        summary.AllPassed = recent.Count == 0;
        summary.FailureDetails = recent;

        foreach (var failure in recent)
        {
            var categoryEnd = failure.IndexOf(']');
            if (categoryEnd > 0)
            {
                var category = failure[1..categoryEnd];
                if (!summary.FailureByCategory.ContainsKey(category))
                    summary.FailureByCategory[category] = 0;
                summary.FailureByCategory[category]++;
            }
        }

        return summary;
    }

    public List<FileValidationReport> GetFileReports()
    {
        return _reports.Values.ToList();
    }
}
