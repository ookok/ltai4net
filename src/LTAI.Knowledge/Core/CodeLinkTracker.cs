using LTAI.Knowledge.Core;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LTAI.Knowledge.Core;

public sealed record CodeLinkIndex
{
    public string SourceFilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string SourceSymbol { get; set; } = "";
    public string TargetSectionId { get; set; } = "";
    public LinkDirection Direction { get; set; }
    public LinkLanguage Language { get; set; }
}

public enum LinkDirection
{
    CodeToSection,
    SectionToCode
}

public enum LinkLanguage
{
    CSharp,
    Python,
    TypeScript,
    JavaScript,
    Rust,
    Go,
    C,
    Cpp,
    Unknown
}

public sealed class CodeLinkTracker
{
    private readonly MarkdownKnowledgeGraph _kg;
    private readonly ConcurrentDictionary<string, List<CodeLinkIndex>> _codeIndex = new();
    private readonly ReaderWriterLockSlim _rwl = new();

    private static readonly Regex LatCommentRx = new(
        @"(?://|#|--)\s*@lat:\s*\[\[([^\]]+)\]\]",
        RegexOptions.Compiled);

    private static readonly Regex CodeSymbolRx = new(
        @"(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed|partial)?\s*(?:class|struct|interface|record|enum|def|fn|func|function|fun)\s+(\w[\w<>]*)",
        RegexOptions.Compiled);

    public CodeLinkTracker(MarkdownKnowledgeGraph kg)
    {
        _kg = kg;
    }

    public List<CodeLinkIndex> ScanFile(string filePath)
    {
        if (!File.Exists(filePath))
            return new();

        var content = File.ReadAllText(filePath);
        return ScanContent(filePath, content);
    }

    public List<CodeLinkIndex> ScanContent(string filePath, string content)
    {
        var results = new List<CodeLinkIndex>();
        var lines = content.Split('\n');
        var lang = DetectLanguage(filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var match = LatCommentRx.Match(lines[i]);
            if (!match.Success) continue;

            var targetId = match.Groups[1].Value.Trim();
            var resolved = ResolveSectionId(targetId);

            var symbol = FindNearestSymbol(lines, i);

            results.Add(new CodeLinkIndex
            {
                SourceFilePath = filePath,
                LineNumber = i + 1,
                SourceSymbol = symbol ?? $"line_{i + 1}",
                TargetSectionId = resolved ?? targetId,
                Direction = LinkDirection.CodeToSection,
                Language = lang
            });

            _kg.AddOrUpdateFile(
                $"lat.md/{Path.GetFileNameWithoutExtension(filePath)}.md",
                $"# Source Code\n\nCode reference from `{filePath}:{i + 1}` → `{resolved ?? targetId}`\n");
        }

        _codeIndex[filePath] = results;
        return results;
    }

    public List<CodeLinkIndex> ScanDirectory(string directoryPath, string pattern = "*.cs")
    {
        var results = new List<CodeLinkIndex>();
        if (!Directory.Exists(directoryPath)) return results;

        foreach (var file in Directory.GetFiles(directoryPath, pattern, SearchOption.AllDirectories))
        {
            results.AddRange(ScanFile(file));
        }
        return results;
    }

    public List<CodeLinkIndex> FindSectionCodeRefs(string sectionId)
    {
        var results = new List<CodeLinkIndex>();
        foreach (var (_, links) in _codeIndex)
        {
            foreach (var link in links)
            {
                if (link.TargetSectionId.EndsWith(sectionId, StringComparison.OrdinalIgnoreCase) ||
                    sectionId.EndsWith(link.TargetSectionId, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(link);
                }
            }
        }
        return results;
    }

    public List<CodeLinkIndex> FindCodeRefsForSymbol(string symbol)
    {
        var results = new List<CodeLinkIndex>();
        foreach (var (_, links) in _codeIndex)
        {
            results.AddRange(links.Where(l =>
                l.SourceSymbol.Contains(symbol, StringComparison.OrdinalIgnoreCase)));
        }
        return results;
    }

    public int CountCodeRefs(string sectionId)
    {
        return _codeIndex.Values
            .Sum(lst => lst.Count(l =>
                l.TargetSectionId.Contains(sectionId, StringComparison.OrdinalIgnoreCase) ||
                sectionId.Contains(l.TargetSectionId, StringComparison.OrdinalIgnoreCase)));
    }

    public string BuildBidirectionalMap()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Bidirectional Link Map");
        sb.AppendLine();

        var sectionToCode = new Dictionary<string, List<CodeLinkIndex>>();
        foreach (var (_, links) in _codeIndex)
        {
            foreach (var link in links)
            {
                if (!sectionToCode.ContainsKey(link.TargetSectionId))
                    sectionToCode[link.TargetSectionId] = new();
                sectionToCode[link.TargetSectionId].Add(link);
            }
        }

        foreach (var (sectionId, refs) in sectionToCode.OrderBy(x => x.Key))
        {
            sb.AppendLine($"## {sectionId}");
            foreach (var r in refs)
            {
                sb.AppendLine($"- `{r.SourceFilePath}:{r.LineNumber}` ({r.Language}: `{r.SourceSymbol}`)");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public List<CodeLinkIndex> GetAllLinks()
    {
        return _codeIndex.Values.SelectMany(x => x).ToList();
    }

    private static string? FindNearestSymbol(string[] lines, int lineIndex)
    {
        for (int lookback = lineIndex; lookback >= Math.Max(0, lineIndex - 10); lookback--)
        {
            var m = CodeSymbolRx.Match(lines[lookback]);
            if (m.Success)
                return m.Groups[1].Value;
        }

        for (int lookahead = lineIndex + 1; lookahead < Math.Min(lines.Length, lineIndex + 10); lookahead++)
        {
            var m = CodeSymbolRx.Match(lines[lookahead]);
            if (m.Success)
                return m.Groups[1].Value;
        }

        return null;
    }

    private static LinkLanguage DetectLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLower();
        return ext switch
        {
            ".cs" => LinkLanguage.CSharp,
            ".py" => LinkLanguage.Python,
            ".ts" => LinkLanguage.TypeScript,
            ".js" or ".jsx" => LinkLanguage.JavaScript,
            ".rs" => LinkLanguage.Rust,
            ".go" => LinkLanguage.Go,
            ".h" or ".c" => LinkLanguage.C,
            ".cpp" or ".hpp" or ".cc" => LinkLanguage.Cpp,
            _ => LinkLanguage.Unknown
        };
    }

    private string? ResolveSectionId(string targetId)
    {
        targetId = targetId.Trim();
        if (targetId.StartsWith("lat.md/"))
            targetId = targetId[7..];

        var section = _kg.GetSection(targetId);
        if (section != null)
            return section.FullId;

        var sections = _kg.Locate(targetId, fuzzy: true);
        if (sections.Count > 0)
            return sections[0].FullId;

        return null;
    }
}
