using System.Collections.Concurrent;

namespace LTAI.Knowledge.Core;

public sealed record TestSpecRequirement(
    string SectionId,
    string FilePath,
    string Heading,
    string SubHeading,
    string Description,
    bool HasCodeReference,
    List<string> ReferencingFiles,
    DateTime CreatedAt)
{
    public bool IsEnforced => HasCodeReference && ReferencingFiles.Count > 0;
}

public sealed class TestSpecEnforcer
{
    private readonly MarkdownKnowledgeGraph _kg;
    private readonly CodeLinkTracker _tracker;
    private readonly ConcurrentDictionary<string, TestSpecRequirement> _specs = new();
    private readonly ConcurrentBag<string> _unenforcedSpecs = new();

    public TestSpecEnforcer(MarkdownKnowledgeGraph kg, CodeLinkTracker tracker)
    {
        _kg = kg;
        _tracker = tracker;
    }

    public async Task EnforceAllAsync(string sourceRoot)
    {
        _specs.Clear();
        _unenforcedSpecs.Clear();

        var files = _kg.GetAllFiles();
        var testSpecFiles = files.Where(f =>
            f.Frontmatter.TryGetValue("require-code-mention", out var rcm) &&
            rcm == "true").ToList();

        foreach (var file in testSpecFiles)
        {
            foreach (var section in file.Sections)
            {
                if (section.SubHeading == null) continue;

                var codeRefs = _tracker.FindSectionCodeRefs(section.FullId);

                var spec = new TestSpecRequirement(
                    SectionId: section.FullId,
                    FilePath: file.FilePath,
                    Heading: section.Heading,
                    SubHeading: section.SubHeading,
                    Description: section.LeadingParagraph,
                    HasCodeReference: codeRefs.Count > 0,
                    ReferencingFiles: codeRefs.Select(r => r.SourceFilePath).Distinct().ToList(),
                    CreatedAt: DateTime.UtcNow);

                _specs[section.FullId] = spec;

                if (!spec.IsEnforced)
                {
                    _unenforcedSpecs.Add(section.FullId);
                }
            }
        }

        if (_unenforcedSpecs.Any() && Directory.Exists(sourceRoot))
        {
            await ScanSourceForMissingRefsAsync(sourceRoot).ConfigureAwait(false);
        }
    }

    private async Task ScanSourceForMissingRefsAsync(string sourceRoot)
    {
        var unenforced = _unenforcedSpecs.ToList();
        foreach (var specId in unenforced)
        {
            if (!_specs.TryGetValue(specId, out var spec))
                continue;

            var searchPattern = spec.SubHeading.Replace(" ", "_").Replace("-", "_");
            var foundFiles = await Task.Run(() =>
                Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(f => File.ReadAllText(f).Contains($"@lat: [[{spec.Heading}", StringComparison.OrdinalIgnoreCase))
                    .ToList());

            if (foundFiles.Count > 0)
            {
                _tracker.ScanFile(foundFiles[0]);
                _unenforcedSpecs.TryTake(out _);

                _specs[specId] = new TestSpecRequirement(
                    spec.SectionId, spec.FilePath, spec.Heading, spec.SubHeading,
                    spec.Description, true, foundFiles, spec.CreatedAt);
            }
        }
    }

    public List<TestSpecRequirement> GetSpecs()
    {
        return _specs.Values.ToList();
    }

    public List<TestSpecRequirement> GetUnenforced()
    {
        return _specs.Values.Where(s => !s.IsEnforced).ToList();
    }

    public double GetCoveragePercentage()
    {
        var total = _specs.Count;
        if (total == 0) return 100;
        var enforced = _specs.Values.Count(s => s.IsEnforced);
        return (double)enforced / total * 100;
    }

    public string GenerateCoverageReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Test Spec Coverage Report");
        sb.AppendLine();
        sb.AppendLine($"- Total specs: {_specs.Count}");
        sb.AppendLine($"- Enforced: {_specs.Values.Count(s => s.IsEnforced)}");
        sb.AppendLine($"- Unenforced: {_specs.Values.Count(s => !s.IsEnforced)}");
        sb.AppendLine($"- Coverage: {GetCoveragePercentage():F1}%");
        sb.AppendLine();

        var unenforced = GetUnenforced();
        if (unenforced.Count > 0)
        {
            sb.AppendLine("## Unenforced Test Specs");
            sb.AppendLine();
            foreach (var spec in unenforced)
            {
                sb.AppendLine($"### {spec.SubHeading}");
                sb.AppendLine($"- Section: `{spec.SectionId}`");
                sb.AppendLine($"- Description: {spec.Description}");
                sb.AppendLine($"- File: `{spec.FilePath}`");
                sb.AppendLine($"- Add `// @lat: [[{spec.SectionId.Split('#').Last()}]]` to the test code");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
