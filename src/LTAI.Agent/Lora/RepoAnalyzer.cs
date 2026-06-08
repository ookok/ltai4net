// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RepoAnalyzer — code repository structure analyzer
//
//  Extracts key context from a code repository: project structure,
//  file tree, languages, namespaces, common imports.
//  Used by CodeLoraAdapter to build the code context.
// ═══════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;

namespace LTAI.Agent.Lora;

/// <summary>
/// Analyzes a code repository's structure and extracts metadata.
/// Supports C#, TypeScript, Python, JavaScript, Go, Rust, Java.
/// </summary>
public sealed partial class RepoAnalyzer
{
    private static readonly HashSet<string> ProjectFilePatterns =
    [
        "*.csproj", "*.sln", "package.json", "Cargo.toml", "go.mod",
        "pom.xml", "build.gradle", "CMakeLists.txt", "Makefile",
        "*.pyproject.toml", "*.config", "*.props", "*.targets",
    ];

    private static readonly HashSet<string> IgnoreDirs =
    [
        "node_modules", "bin", "obj", ".git", ".svn", ".vs",
        "packages", "__pycache__", ".venv", "venv", "dist", "build",
        ".next", ".nuxt", "coverage", ".vscode", ".idea",
        ".livingtree", ".github",
    ];

    private static readonly HashSet<string> CSharpExtensions = [".cs", ".csx"];
    private static readonly HashSet<string> TSExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs"];
    private static readonly HashSet<string> PythonExtensions = [".py", ".pyi"];
    private static readonly HashSet<string> GoExtensions = [".go"];
    private static readonly HashSet<string> RustExtensions = [".rs"];
    private static readonly HashSet<string> JavaExtensions = [".java", ".kt", ".kts"];

    /// <summary>
    /// Analyze a repository path and build code context.
    /// </summary>
    public async Task<CodeContext> AnalyzeAsync(string repoPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(repoPath))
            return new CodeContext { RepoPath = repoPath };

        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var projectFiles = new List<string>();
        var fileCountByLang = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalFiles = 0;
        long totalLoc = 0;

        var entries = await WalkDirectoryAsync(repoPath, ct);
        foreach (var entry in entries)
        {
            if (entry.IsDirectory) continue;

            var ext = Path.GetExtension(entry.Path).ToLowerInvariant();
            var lang = DetectLanguage(ext);
            if (lang == null) continue;

            totalFiles++;
            languages.Add(lang);
            fileCountByLang[lang] = fileCountByLang.TryGetValue(lang, out var c) ? c + 1 : 1;

            // Read first 20 lines for namespace/import extraction
            if (entry.LinesRead > 0)
            {
                totalLoc += entry.LinesRead;
                ExtractPatterns(entry.Content, lang, namespaces, imports);
            }

            // Identify project files
            if (IsProjectFile(entry.Path))
                projectFiles.Add(entry.Path);
        }

        return new CodeContext
        {
            RepoPath = repoPath,
            Languages = languages,
            ProjectFiles = projectFiles.AsReadOnly(),
            Namespaces = namespaces.Take(20).ToList().AsReadOnly(),
            CommonImports = imports
                .OrderByDescending(kv => kv.Value)
                .Take(20)
                .Select(kv => kv.Key)
                .ToList()
                .AsReadOnly(),
            FileCountByLang = fileCountByLang,
            TotalFiles = totalFiles,
            LoC = totalLoc,
            AnalyzedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Detect programming language from file extension.
    /// </summary>
    public static string? DetectLanguage(string extension)
    {
        extension = extension.ToLowerInvariant();
        if (CSharpExtensions.Contains(extension)) return "C#";
        if (TSExtensions.Contains(extension)) return "TypeScript/JavaScript";
        if (PythonExtensions.Contains(extension)) return "Python";
        if (GoExtensions.Contains(extension)) return "Go";
        if (RustExtensions.Contains(extension)) return "Rust";
        if (JavaExtensions.Contains(extension)) return "Java";
        return null;
    }

    private static bool IsProjectFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name is ".csproj" or ".sln" or "package.json" or "cargo.toml"
            or "go.mod" or "pom.xml" or "build.gradle" or "cmakelists.txt"
            or "makefile" or "global.json" or "directory.build.props"
            or "directory.build.targets";
    }

    private static void ExtractPatterns(
        string content, string lang,
        HashSet<string> namespaces,
        Dictionary<string, int> imports)
    {
        switch (lang)
        {
            case "C#":
                // Extract using statements
                foreach (Match m in UsingPattern().Matches(content))
                {
                    var ns = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(ns) && !ns.StartsWith("System"))
                        imports[ns] = imports.TryGetValue(ns, out var c) ? c + 1 : 1;
                }
                // Extract namespace declarations
                foreach (Match m in NamespacePattern().Matches(content))
                    namespaces.Add(m.Groups[1].Value.Trim());
                break;

            case "TypeScript/JavaScript":
                foreach (Match m in ImportPattern().Matches(content))
                {
                    var from = m.Groups[1].Value.Trim().Trim('\'', '"');
                    if (!string.IsNullOrEmpty(from) && !from.StartsWith(".") && !from.StartsWith("/"))
                        imports[from] = imports.TryGetValue(from, out var c) ? c + 1 : 1;
                }
                break;

            case "Python":
                foreach (Match m in PythonImportPattern().Matches(content))
                {
                    var mod = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(mod))
                        imports[mod] = imports.TryGetValue(mod, out var c) ? c + 1 : 1;
                }
                break;
        }
    }

    private async Task<List<(string Path, bool IsDirectory, string Content, int LinesRead)>>
        WalkDirectoryAsync(string root, CancellationToken ct)
    {
        var results = new List<(string, bool, string, int)>();
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();

            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (!IgnoreDirs.Contains(name) && !name.StartsWith("."))
                        queue.Enqueue(d);
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (DetectLanguage(ext) == null && !IsProjectFile(file)) continue;

                    // Read first 30 lines for pattern extraction
                    var content = "";
                    var linesRead = 0;
                    try
                    {
                        using var sr = new StreamReader(file);
                        var sb = new System.Text.StringBuilder();
                        string? line;
                        while ((line = sr.ReadLine()) != null && linesRead < 30)
                        {
                            sb.AppendLine(line);
                            linesRead++;
                        }
                        content = sb.ToString();
                    }
                    catch { /* skip unreadable files */ }

                    results.Add((file, false, content, linesRead));
                }
            }
            catch (UnauthorizedAccessException) { /* skip */ }
        }

        return results;
    }

    [GeneratedRegex(@"^\s*using\s+([\w.]+);", RegexOptions.Multiline)]
    private static partial Regex UsingPattern();

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)\s*{?", RegexOptions.Multiline)]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(@"import\s+(?:\{[^}]*\}\s+from\s+)?['""]([^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex ImportPattern();

    [GeneratedRegex(@"^\s*(?:from\s+)?(?:import\s+)([\w.]+)", RegexOptions.Multiline)]
    private static partial Regex PythonImportPattern();
}
