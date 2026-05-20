using System.Text.RegularExpressions;
using LTAI.Vector.Interfaces;

namespace LTAI.Vector.Knowledge;

public sealed record KnowledgeSection(
    string SectionId,
    string FilePath,
    string Heading,
    string? SubHeading,
    string Body,
    string LeadingParagraph,
    List<string> WikiLinks,
    Dictionary<string, string> Frontmatter,
    int LineStart,
    int LineEnd)
{
    public string FullId => string.IsNullOrEmpty(SubHeading)
        ? $"{FilePath}#{Heading}"
        : $"{FilePath}#{Heading}#{SubHeading}";
}

public sealed record KnowledgeFile(
    string RelativePath,
    string Content,
    List<KnowledgeSection> Sections,
    List<string> CodeRefs,
    Dictionary<string, string> Frontmatter)
{
    public string FilePath => RelativePath;
}

public sealed record BrokenLink(
    string SourceSection,
    string TargetLink,
    BrokenLinkType Type,
    string Detail)
{
    public string Message => Type switch
    {
        BrokenLinkType.WikiLink => $"Wiki link target not found: {TargetLink}",
        BrokenLinkType.CodeRef => $"Source code symbol not found: {TargetLink}",
        BrokenLinkType.CodeMention => $"Test spec not referenced in code: {TargetLink}",
        BrokenLinkType.MissingParagraph => $"Section missing leading paragraph: {SourceSection}",
        BrokenLinkType.ParagraphTooLong => $"Leading paragraph >250 chars: {SourceSection}",
        _ => Detail
    };
}

public enum BrokenLinkType
{
    WikiLink,
    CodeRef,
    CodeMention,
    MissingParagraph,
    ParagraphTooLong
}

public sealed class LatCheckResult
{
    public bool AllValid { get; set; }
    public List<BrokenLink> Errors { get; set; } = new();
    public int FilesScanned { get; set; }
    public int SectionsScanned { get; set; }
    public int WikiLinksChecked { get; set; }
    public int CodeRefsChecked { get; set; }
    public int TestSpecsEnforced { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MarkdownKnowledgeGraph
{
    private readonly string _rootPath;
    private readonly string _latMdPath;
    private readonly Dictionary<string, KnowledgeFile> _files = new();
    private readonly Dictionary<string, KnowledgeSection> _sectionsById = new();
    private readonly ReaderWriterLockSlim _rwl = new();
    private readonly IEmbeddingBackend? _embedding;
    private readonly IVectorStore? _vectorStore;
    private bool _initialized;

    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline);
    private static readonly Regex WikiLinkRegex = new(@"\[\[([^\]]+)\]\]");
    private static readonly Regex FrontmatterRegex = new(@"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline);
    private static readonly Regex YamlKeyRegex = new(@"^(\w[\w-]*)\s*:\s*(.+)$", RegexOptions.Multiline);
    private static readonly Regex FirstParagraphRegex = new(@"^#+\s+.+\n\n((?:(?!#).)*?)(?=\n(#+\s|```|\n$|$))", RegexOptions.Singleline);

    public MarkdownKnowledgeGraph(string rootPath, IEmbeddingBackend? embedding = null, IVectorStore? vectorStore = null)
    {
        _rootPath = rootPath;
        _latMdPath = Path.Combine(rootPath, "lat.md");
        _embedding = embedding;
        _vectorStore = vectorStore;
    }

    public void Initialize()
    {
        _rwl.EnterWriteLock();
        try
        {
            _files.Clear();
            _sectionsById.Clear();
            if (!Directory.Exists(_latMdPath))
                Directory.CreateDirectory(_latMdPath);

            foreach (var filePath in Directory.GetFiles(_latMdPath, "*.md", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(_rootPath, filePath).Replace('\\', '/');
                var content = File.ReadAllText(filePath);
                var (sections, frontmatter, codeRefs) = ParseFile(relPath, content);

                _files[relPath] = new KnowledgeFile(relPath, content, sections, codeRefs, frontmatter);

                foreach (var section in sections)
                {
                    _sectionsById[section.FullId] = section;
                }
            }
            _initialized = true;
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public KnowledgeSection? GetSection(string sectionId)
    {
        _rwl.EnterReadLock();
        try
        {
            _sectionsById.TryGetValue(sectionId, out var section);
            return section;
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public List<KnowledgeSection> Locate(string query, bool fuzzy = false)
    {
        _rwl.EnterReadLock();
        try
        {
            var results = new List<KnowledgeSection>();
            foreach (var (id, section) in _sectionsById)
            {
                if (fuzzy)
                {
                    if (id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        section.Heading.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        section.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(section);
                    }
                }
                else
                {
                    if (id.Equals(query, StringComparison.OrdinalIgnoreCase))
                        results.Add(section);
                }
            }
            return results;
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public List<KnowledgeSection> FindRefs(string sectionId)
    {
        _rwl.EnterReadLock();
        try
        {
            var refs = new List<KnowledgeSection>();
            foreach (var (_, section) in _sectionsById)
            {
                foreach (var link in section.WikiLinks)
                {
                    var resolved = ResolveLink(section.FilePath, link);
                    if (string.Equals(resolved, sectionId, StringComparison.OrdinalIgnoreCase))
                    {
                        refs.Add(section);
                        break;
                    }
                }
            }
            return refs;
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public async Task<List<(KnowledgeSection Section, float Score)>> SearchAsync(string query, int topK = 10)
    {
        if (_embedding == null || _vectorStore == null)
            return Locate(query, fuzzy: true).Select(s => (s, 1.0f)).Take(topK).ToList();

        var qVector = (await _embedding.EmbedAsync(new[] { query }))[0];

        var results = new List<(KnowledgeSection, float)>();
        _rwl.EnterReadLock();
        try
        {
            foreach (var (_, section) in _sectionsById)
            {
                var score = CosineSimilarity(qVector, EmbedSection(section));
                if (score > 0.3f)
                    results.Add((section, score));
            }
        }
        finally
        {
            _rwl.ExitReadLock();
        }

        return results.OrderByDescending(r => r.Item2).Take(topK).ToList();
    }

    public string ExpandPrompt(string prompt)
    {
        var matches = WikiLinkRegex.Matches(prompt);
        if (matches.Count == 0) return prompt;

        var expanded = prompt;
        _rwl.EnterReadLock();
        try
        {
            foreach (Match match in matches)
            {
                var link = match.Groups[1].Value;
                foreach (var (id, section) in _sectionsById)
                {
                    if (id.Contains(link, StringComparison.OrdinalIgnoreCase))
                    {
                        var replacement = $"<!-- BEGIN {link} -->\n{section.Body.Trim()}\n<!-- END {link} -->";
                        expanded = expanded.Replace(match.Value, replacement);
                        break;
                    }
                }
            }
        }
        finally
        {
            _rwl.ExitReadLock();
        }

        return expanded;
    }

    public LatCheckResult Check(bool scanCodeRefs = true)
    {
        _rwl.EnterReadLock();
        try
        {
            var result = new LatCheckResult
            {
                FilesScanned = _files.Count,
                SectionsScanned = _sectionsById.Count
            };

            foreach (var (id, section) in _sectionsById)
            {
                if (string.IsNullOrWhiteSpace(section.LeadingParagraph))
                {
                    result.Errors.Add(new BrokenLink(id, id, BrokenLinkType.MissingParagraph,
                        $"Section '{section.Heading}' has no leading paragraph"));
                }
                else if (section.LeadingParagraph.Length > 250)
                {
                    result.Errors.Add(new BrokenLink(id, id, BrokenLinkType.ParagraphTooLong,
                        $"Leading paragraph is {section.LeadingParagraph.Length} chars (max 250)"));
                }

                foreach (var link in section.WikiLinks)
                {
                    result.WikiLinksChecked++;
                    var resolved = ResolveLink(section.FilePath, link);

                    if (resolved != null && _sectionsById.ContainsKey(resolved))
                        continue;

                    var isCodeRef = link.Contains("/") && !link.EndsWith(".md") &&
                                    (link.Contains(".ts") || link.Contains(".js") || link.Contains(".py") ||
                                     link.Contains(".rs") || link.Contains(".go") || link.Contains(".cs") ||
                                     link.Contains(".h") || link.Contains(".cpp"));

                    if (!isCodeRef)
                    {
                        result.Errors.Add(new BrokenLink(id, link, BrokenLinkType.WikiLink,
                            $"Target '{link}' not found in knowledge graph"));
                    }
                }
            }

            if (scanCodeRefs)
            {
                foreach (var (_, file) in _files)
                {
                    foreach (var codeRef in file.CodeRefs)
                    {
                        result.CodeRefsChecked++;

                        var resolved = codeRef.StartsWith("lat.md/")
                            ? codeRef.Substring(7)
                            : codeRef;

                        var found = _sectionsById.ContainsKey(resolved);
                        if (!found)
                        {
                            found = _sectionsById.Keys.Any(k =>
                                k.EndsWith(resolved, StringComparison.OrdinalIgnoreCase));
                        }

                        if (!found)
                        {
                            result.Errors.Add(new BrokenLink(file.FilePath, codeRef, BrokenLinkType.CodeRef,
                                $"Code ref '{codeRef}' does not resolve to any section"));
                        }
                    }

                    if (file.Frontmatter.TryGetValue("require-code-mention", out var rcm) && rcm == "true")
                    {
                        foreach (var section in file.Sections)
                        {
                            if (section.SubHeading == null) continue;

                            result.TestSpecsEnforced++;
                            result.Errors.Add(new BrokenLink(section.FullId, section.FullId,
                                BrokenLinkType.CodeMention,
                                $"Test spec '{section.FullId}' requires code mention (not yet validated in source)"));
                        }
                    }
                }
            }

            result.AllValid = result.Errors.Count == 0;

            return result;
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public KnowledgeFile? AddOrUpdateFile(string relativePath, string content)
    {
        _rwl.EnterWriteLock();
        try
        {
            var fullPath = Path.Combine(_rootPath, relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);

            var relative = relativePath.Replace('\\', '/');
            var (sections, frontmatter, codeRefs) = ParseFile(relative, content);

            _files[relative] = new KnowledgeFile(relative, content, sections, codeRefs, frontmatter);

            foreach (var section in sections)
            {
                _sectionsById[section.FullId] = section;
            }

            var existingIds = _sectionsById.Keys
                .Where(k => k.StartsWith(relative + "#"))
                .ToList();

            foreach (var oldId in existingIds)
            {
                if (sections.All(s => s.FullId != oldId))
                    _sectionsById.Remove(oldId);
            }

            return _files[relative];
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public void RemoveFile(string relativePath)
    {
        _rwl.EnterWriteLock();
        try
        {
            relativePath = relativePath.Replace('\\', '/');
            _files.Remove(relativePath);

            var idsToRemove = _sectionsById.Keys
                .Where(k => k.StartsWith(relativePath + "#"))
                .ToList();

            foreach (var id in idsToRemove)
                _sectionsById.Remove(id);
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public List<KnowledgeFile> GetAllFiles()
    {
        _rwl.EnterReadLock();
        try
        {
            return _files.Values.ToList();
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    private static (List<KnowledgeSection>, Dictionary<string, string>, List<string>) ParseFile(
        string filePath, string content)
    {
        var frontmatter = new Dictionary<string, string>();
        var codeRefs = new List<string>();

        var fmMatch = FrontmatterRegex.Match(content);
        var parseStart = 0;
        if (fmMatch.Success)
        {
            parseStart = fmMatch.Length;
            var fmContent = fmMatch.Groups[1].Value;
            foreach (Match m in YamlKeyRegex.Matches(fmContent))
            {
                var key = m.Groups[1].Value.Trim();
                var val = m.Groups[2].Value.Trim();
                if (key == "require-code-mention" || key == "lat.require-code-mention")
                {
                    frontmatter["require-code-mention"] = val;
                }
                else
                {
                    frontmatter[key] = val;
                }
            }
        }

        var sections = new List<KnowledgeSection>();
        var headingMatches = HeadingRegex.Matches(content, parseStart);
        var headings = new List<(int Index, int Level, string Text)>();

        foreach (Match m in headingMatches)
        {
            headings.Add((m.Index, m.Groups[1].Value.Length, m.Groups[2].Value.Trim()));
        }

        for (int i = 0; i < headings.Count; i++)
        {
            var (start, level, text) = headings[i];
            var contentStart = start + content[start..].IndexOf('\n') + 1;
            var contentEnd = i + 1 < headings.Count ? headings[i + 1].Index : content.Length;
            var body = content[contentStart..contentEnd].Trim();

            var allWikiLinks = new List<string>();
            foreach (Match wl in WikiLinkRegex.Matches(content[start..contentEnd]))
                allWikiLinks.Add(wl.Groups[1].Value);

            var leadingParagraph = ExtractLeadingParagraph(content, start, level);

            var parentHeading = "";
            KnowledgeSection? parent = null;
            for (int j = i - 1; j >= 0; j--)
            {
                if (headings[j].Level < level)
                {
                    parentHeading = headings[j].Text;
                    parent = sections.FirstOrDefault(s =>
                        s.Heading == parentHeading && s.LineStart <= headings[j].Index);
                    break;
                }
            }

            var sectionId = level == 1
                ? text
                : parentHeading;

            sections.Add(new KnowledgeSection(
                SectionId: sectionId,
                FilePath: filePath,
                Heading: level == 1 ? text : sectionId,
                SubHeading: level > 1 ? text : null,
                Body: body,
                LeadingParagraph: leadingParagraph,
                WikiLinks: allWikiLinks,
                Frontmatter: frontmatter,
                LineStart: start,
                LineEnd: contentEnd));
        }

        return (sections, frontmatter, codeRefs);
    }

    private static string ExtractLeadingParagraph(string content, int headingStart, int headingLevel)
    {
        var afterHeading = content[headingStart..];
        var nl = afterHeading.IndexOf('\n');
        if (nl < 0) return "";

        var afterNewline = afterHeading[(nl + 1)..].TrimStart();
        if (afterNewline.StartsWith("```") || afterNewline.StartsWith("|") ||
            afterNewline.StartsWith("- ") || afterNewline.StartsWith("#"))
            return "";

        var nextBlock = afterNewline.IndexOf("\n\n");
        if (nextBlock < 0) nextBlock = afterNewline.Length;

        return afterNewline[..nextBlock].Trim();
    }

    private string? ResolveLink(string sourceFilePath, string link)
    {
        link = link.Split('|')[0].Trim();

        if (link.Contains('/') && link.Contains('.'))
        {
            if (link.EndsWith(".md"))
            {
                var parts = link.Split('#', 2);
                var filePath = parts[0];
                var section = parts.Length > 1 ? parts[1] : null;

                var resolvedFile = ResolveRelativePath(sourceFilePath, filePath);
                if (section != null)
                    return $"{resolvedFile}#{section}";
                return resolvedFile;
            }
            return null;
        }

        var hashIndex = link.IndexOf('#');
        if (hashIndex > 0)
        {
            var fileRef = link[..hashIndex];
            var sectionRef = link[(hashIndex + 1)..];

            if (_files.TryGetValue(fileRef, out var file))
            {
                if (string.IsNullOrEmpty(sectionRef))
                    return file.FilePath;

                return $"{file.FilePath}#{sectionRef}";
            }

            foreach (var (path, _) in _files)
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName == fileRef)
                    return $"{path}#{sectionRef}";
            }
        }

        foreach (var (id, section) in _sectionsById)
        {
            if (id.EndsWith($"#{link}", StringComparison.OrdinalIgnoreCase))
                return id;

            if (section.Heading.Equals(link, StringComparison.OrdinalIgnoreCase) ||
                (section.SubHeading?.Equals(link, StringComparison.OrdinalIgnoreCase) ?? false))
                return id;
        }

        return null;
    }

    private static string ResolveRelativePath(string sourcePath, string targetPath)
    {
        var sourceDir = Path.GetDirectoryName(sourcePath) ?? "";
        var combined = Path.Combine(sourceDir, targetPath).Replace('\\', '/');
        var normalized = combined;
        while (normalized.Contains("/../") || normalized.Contains("/./"))
        {
            normalized = Regex.Replace(normalized, @"/\./", "/");
            normalized = Regex.Replace(normalized, @"[^/]+/\.\./", "");
        }
        return normalized;
    }

    private static float[] EmbedSection(KnowledgeSection section)
    {
        var text = $"{section.Heading} {section.SubHeading ?? ""} {section.LeadingParagraph}";
        var hash = (uint)text.GetHashCode();
        var vec = new float[64];
        for (int i = 0; i < 64; i++)
        {
            var h = (hash * (uint)(i + 1)) % 251;
            vec[i] = (float)Math.Sin(h * 0.1) * 0.5f + 0.5f;
        }
        var norm = (float)Math.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
        {
            for (int i = 0; i < 64; i++) vec[i] /= norm;
        }
        return vec;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        var len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / ((float)Math.Sqrt(normA) * (float)Math.Sqrt(normB));
    }
}
