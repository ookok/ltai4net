// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ContentFilter — multi-layer guard against garbage data
//
//  Entry-point defense for the knowledge graph.
//  Three layers of filtering:
//
//  Layer 1: PathFilter — reject known garbage paths/patterns
//  Layer 2: ContentFilter — reject noise content (binary, logs, temp)
//  Layer 3: QualityFilter — score & reject low-quality extractions
//
//  Each layer is independently configurable so callers can tune
//  strictness per use case (e.g. DocumentIndexer can be more
//  permissive than KnowledgeExtractor).
// ═══════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Indexing;

/// <summary>
/// Content verification result.
/// </summary>
public enum FilterVerdict
{
    /// <summary>Content passes all active filter layers.</summary>
    Allowed,
    /// <summary>Path matches a known garbage pattern.</summary>
    Blocked_Path,
    /// <summary>Content is binary or low-entropy noise.</summary>
    Blocked_Binary,
    /// <summary>Content is too short or too long.</summary>
    Blocked_Size,
    /// <summary>Content is a log/timestamp-heavy file.</summary>
    Blocked_LogNoise,
    /// <summary>Quality score below the active threshold.</summary>
    Blocked_LowQuality,
}

/// <summary>
/// Multi-layer content filter for knowledge graph ingestion.
/// Layer 1 (PathFilter), Layer 2 (ContentFilter), Layer 3 (QualityFilter).
/// </summary>
public static partial class ContentFilter
{
    private static readonly ILogger _logger =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<object>.Instance;

    // ═══════════════════════════════════════════════════════════
    //  Layer 1: Path-based filtering
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Directories that are always skipped (any naming pattern, any depth).
    /// </summary>
    private static readonly HashSet<string> SkipDirPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        // Build / cache
        "bin", "obj", "dist", "build", "out", "target", "cmake-build-debug",
        "cmake-build-release", ".next", ".nuxt", ".output",
        // Dependencies
        "node_modules", "packages", "vendor", ".venv", "venv", "__pycache__",
        "bower_components", "jspm_packages",
        // Version control
        ".git", ".svn", ".hg",
        // IDE
        ".vs", ".vscode", ".idea", ".eclipse",
        // Logs & output
        "logs", "log", "tmp", "temp", "coverage", ".nyc_output",
        // LLM / temp
        ".livingtree", ".sandbox",
        // AOT compilation artifacts
        "aot",
    };

    /// <summary>
    /// File extensions that are never indexed (binary + known garbage).
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Executables
        ".exe", ".dll", ".so", ".dylib", ".wasm", ".o", ".a", ".lib",
        ".obj", ".sys", ".bin",
        // Archives
        ".zip", ".tar", ".gz", ".bz2", ".xz", ".7z", ".rar",
        ".nupkg", ".whl", ".jar", ".egg",
        // Images (no text content)
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg",
        ".webp", ".tiff", ".psd",
        // Audio/Video
        ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac", ".ogg",
        // Documents (binary format—use dedicated readers)
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf",
        ".pub", ".vsd",
        // Fonts
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        // Database
        ".db", ".sqlite", ".mdf", ".ldf",
        // Cache / lock files
        ".lock", ".cache", ".map", ".pak", ".unity",
        // Compiled
        ".class", ".pyc", ".pyo", ".pyd",
        // LLM model files
        ".onnx", ".bin", ".gguf", ".safetensors",
    };

    /// <summary>
    /// Allowed file extensions for knowledge graph indexing.
    /// Compared to DocumentIndexer.TextExts, this list explicitly
    /// removes ".log" and adds source code extensions.
    /// </summary>
    private static readonly HashSet<string> AllowedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documentation
        ".md", ".mdx", ".txt", ".rst", ".adoc", ".asciidoc",
        // Config
        ".json", ".yaml", ".yml", ".xml", ".toml", ".ini", ".cfg", ".conf",
        ".env.example", ".editorconfig",
        // Source code (for knowledge extraction)
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".go", ".rs", ".rb",
        ".java", ".kt", ".kts", ".swift", ".php", ".cpp", ".c", ".h",
        ".hpp", ".css", ".scss", ".less", ".sh", ".bash", ".ps1",
        ".sql", ".graphql", ".proto", ".gradle", ".sln",
        // Web
        ".html", ".htm", ".vue", ".svelte", ".astro",
        // Others
        ".dockerfile", ".csproj", ".props", ".targets", ".ruleset",
    };

    // ═══════════════════════════════════════════════════════════
    //  Layer 2: Content-based filtering
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Min/max content length for indexing.
    /// </summary>
    private const int MinContentLength = 50;    // below this = meaningless
    private const int MaxContentLength = 100_000; // above this = too noisy

    /// <summary>
    /// Maximum ratio of numeric/digit characters allowed.
    /// Log files are often >30% digits (timestamps, line numbers).
    /// </summary>
    private const double MaxDigitRatio = 0.25;

    /// <summary>
    /// Maximum ratio of special/symbol characters.
    /// Stack traces and binary-like noise are >20% symbols.
    /// </summary>
    private const double MaxSymbolRatio = 0.20;

    /// <summary>
    /// Minimum newline count for log-file detection.
    /// A file with >80% lines containing "INFO|WARN|ERROR|DEBUG"
    /// is classified as a log file.
    /// </summary>
    private const double MaxLogLineRatio = 0.60;

    /// <summary>
    /// Maximum line length — log files often have very long lines.
    /// If average line length exceeds this, it's likely minified/compressed.
    /// </summary>
    private const int MaxAvgLineLength = 500;

    /// <summary>Minimum unique words ratio: avoid repetitive content.</summary>
    private const double MinUniqueWordRatio = 0.20;

    /// <summary>
    /// Character categories for content analysis.
    /// </summary>
    [Flags]
    private enum CharCategory
    {
        None = 0,
        Letter = 1,
        Digit = 2,
        Whitespace = 4,
        Symbol = 8,
        Control = 16,
    }

    // ═══════════════════════════════════════════════════════════
    //  Layer 3: Quality scoring
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Quality score below which extracted knowledge is rejected.
    /// Range: 0.0 (useless) to 1.0 (high quality).
    /// </summary>
    private const double QualityThreshold = 0.3;

    // ═══════════════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Full screening: path + content + quality. Use before upserting.
    /// </summary>
    public static FilterVerdict ScreenPath(string relativePath)
    {
        // Check directory patterns
        var parts = relativePath.Split('/', '\\');
        foreach (var part in parts)
        {
            if (SkipDirPatterns.Contains(part))
            {
                _logger.LogTrace("ContentFilter: blocked path '{Path}' (dir: {Dir})", relativePath, part);
                return FilterVerdict.Blocked_Path;
            }
        }

        // Check extension
        var ext = Path.GetExtension(relativePath);
        if (string.IsNullOrEmpty(ext) || BinaryExtensions.Contains(ext))
        {
            _logger.LogTrace("ContentFilter: blocked path '{Path}' (ext: {Ext})", relativePath, ext);
            return FilterVerdict.Blocked_Path;
        }

        // Not in allowed text extensions? Still allow via dedicated readers
        // (e.g. .docx via OfficeDocumentReader, .pdf via PdfPig).
        return FilterVerdict.Allowed;
    }

    /// <summary>
    /// Screen file content for noise before indexing.
    /// </summary>
    public static FilterVerdict ScreenContent(string content, string? fileName = null)
    {
        if (string.IsNullOrEmpty(content))
            return FilterVerdict.Blocked_Size;

        // Check length
        if (content.Length < MinContentLength)
            return FilterVerdict.Blocked_Size;
        if (content.Length > MaxContentLength)
            return FilterVerdict.Blocked_Size;

        // Check for binary content (null bytes in first 1KB)
        var firstKb = content.Length > 1024 ? content[..1024] : content;
        if (firstKb.Contains('\0'))
            return FilterVerdict.Blocked_Binary;

        // Check if it's a log file
        var lines = content.Split('\n');
        if (lines.Length > 5)
        {
            var logPatterns = 0;
            foreach (var line in lines.Take(50))
            {
                if (LogLinePattern().IsMatch(line))
                    logPatterns++;
            }
            if ((double)logPatterns / Math.Min(lines.Length, 50) > MaxLogLineRatio)
            {
                _logger.LogTrace("ContentFilter: blocked as log noise '{File}' ({Ratio:P0} log lines)",
                    fileName ?? "", (double)logPatterns / Math.Min(lines.Length, 50));
                return FilterVerdict.Blocked_LogNoise;
            }
        }

        // Categorize characters
        int letters = 0, digits = 0, symbols = 0, total = 0;
        int maxLineLen = 0, totalLineLen = 0;
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int wordCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            totalLineLen += trimmed.Length;
            if (trimmed.Length > maxLineLen) maxLineLen = trimmed.Length;

            foreach (var ch in trimmed)
            {
                total++;
                if (char.IsLetter(ch)) letters++;
                else if (char.IsDigit(ch)) digits++;
                else if (char.IsWhiteSpace(ch)) { /* skip */ }
                else symbols++;
            }

            // Count words for uniqueness
            foreach (var word in trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 3)
                {
                    words.Add(word);
                    wordCount++;
                }
            }
        }

        if (total == 0) return FilterVerdict.Blocked_Binary;

        var digitRatio = (double)digits / total;
        var symbolRatio = (double)symbols / total;
        var avgLineLength = lines.Length > 0 ? totalLineLen / lines.Length : 0;
        var uniqueWordRatio = wordCount > 0 ? (double)words.Count / wordCount : 0;

        // Digit ratio too high → log/timestamp noise
        if (digitRatio > MaxDigitRatio)
        {
            _logger.LogTrace("ContentFilter: blocked '{File}' (digit ratio {Ratio:P0})",
                fileName ?? "", digitRatio);
            return FilterVerdict.Blocked_LogNoise;
        }

        // Symbol ratio too high → binary-like or stack trace
        if (symbolRatio > MaxSymbolRatio)
        {
            _logger.LogTrace("ContentFilter: blocked '{File}' (symbol ratio {Ratio:P0})",
                fileName ?? "", symbolRatio);
            return FilterVerdict.Blocked_LogNoise;
        }

        // Average line length too high → minified/binary
        if (avgLineLength > MaxAvgLineLength)
        {
            _logger.LogTrace("ContentFilter: blocked '{File}' (avg line len {Len})",
                fileName ?? "", avgLineLength);
            return FilterVerdict.Blocked_LogNoise;
        }

        // Content is not unique enough → boilerplate/template
        if (uniqueWordRatio < MinUniqueWordRatio && wordCount > 20)
        {
            _logger.LogTrace("ContentFilter: blocked '{File}' (unique ratio {Ratio:P0})",
                fileName ?? "", uniqueWordRatio);
            return FilterVerdict.Blocked_LowQuality;
        }

        return FilterVerdict.Allowed;
    }

    /// <summary>
    /// Screen LLM-extracted knowledge for quality before upserting to KgStore.
    /// </summary>
    public static FilterVerdict ScreenExtraction(string concept, string summary)
    {
        if (string.IsNullOrWhiteSpace(concept) || concept.Length < 2)
            return FilterVerdict.Blocked_LowQuality;

        if (string.IsNullOrWhiteSpace(summary) || summary.Length < 10)
            return FilterVerdict.Blocked_LowQuality;

        // Score the extraction quality
        var quality = ScoreExtractionQuality(concept, summary);
        if (quality < QualityThreshold)
        {
            _logger.LogTrace("ContentFilter: blocked extraction '{Concept}' (quality {Score:P0})",
                concept, quality);
            return FilterVerdict.Blocked_LowQuality;
        }

        return FilterVerdict.Allowed;
    }

    /// <summary>
    /// Check if a file extension should be indexed for knowledge extraction.
    /// </summary>
    public static bool IsAllowedExtension(string extension)
        => !string.IsNullOrEmpty(extension)
           && AllowedTextExtensions.Contains(extension.StartsWith(".") ? extension : "." + extension);

    /// <summary>
    /// Check if a directory name should be skipped.
    /// </summary>
    public static bool IsSkippedDirectory(string dirName)
        => SkipDirPatterns.Contains(dirName);

    /// <summary>
    /// Recommended TextExts for DocumentIndexer (replaces the existing one
    /// that includes ".log").
    /// </summary>
    public static HashSet<string> GetIndexerExtensions()
        => new(AllowedTextExtensions, StringComparer.OrdinalIgnoreCase);

    // ═══════════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Score extraction quality based on content characteristics.
    /// Higher score = more likely to be useful knowledge.
    /// </summary>
    private static double ScoreExtractionQuality(string concept, string summary)
    {
        double score = 0.5; // neutral baseline

        // Length bonus: longer = more informative
        if (concept.Length > 5) score += 0.1;
        if (concept.Length > 15) score += 0.05;
        if (summary.Length > 30) score += 0.1;
        if (summary.Length > 100) score += 0.05;

        // Contains actionable content
        if (char.IsUpper(concept[0])) score += 0.05; // proper noun
        if (concept.Contains(' ') || concept.Contains('_') || concept.Contains('.'))
            score += 0.05; // multi-word concept

        // Contains code-like patterns (good for code KG)
        if (CodePattern().IsMatch(concept) || CodePattern().IsMatch(summary))
            score += 0.1;

        // Penalize generic concepts
        if (GenericConceptPattern().IsMatch(concept))
            score -= 0.2;

        // Penalize error/exception content
        if (ErrorPattern().IsMatch(concept) || ErrorPattern().IsMatch(summary))
            score -= 0.3;

        return Math.Clamp(score, 0.0, 1.0);
    }

    [GeneratedRegex(@"\b(ERROR|WARN(ING)?|FATAL|TRACE|DEBUG|Exception|NullReference|StackOverflow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LogLinePattern();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+\b")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\b(the|a|an|this|that|it|is|was|are|be|have|has|do|does|thing|stuff|info|note|item|entry)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericConceptPattern();

    [GeneratedRegex(@"\b(Exception|Error|Failure|Failed|Invalid|Unexpected|Cannot|Could not|Stack trace|NullReference)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPattern();
}
