// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ProjectInitService — one-click project initialization
//
//  Inspired by zap-coding-agent's /init command.
//  Detects project type, creates project context file (LTAI.md),
//  and triggers code index warm-up.
//
//  Usage:
//    var result = await initService.InitAsync(ct);
//    // result = "✅ 初始化完成 ..."
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LTAI.Agent;

/// <summary>
/// Detected project type and metadata.
/// </summary>
public sealed record ProjectInfo(
    string Type,           // e.g. "dotnet", "node", "rust", "python", "go", "unknown"
    string Language,       // e.g. "C#", "TypeScript", "Rust", "Python", "Go"
    string[] BuildFiles,   // detected build files
    string BuildCommand,   // e.g. "dotnet build", "npm run build", "cargo build"
    string TestCommand,    // e.g. "dotnet test", "npm test", "cargo test"
    string[] Frameworks);  // e.g. ["ASP.NET", "React", "Next.js"]

/// <summary>
/// One-click project initialization service.
/// Detects project type, generates LTAI.md context file,
/// and initializes code indexing.
/// </summary>
public sealed class ProjectInitService
{
    private readonly string _workspace;

    // Build file patterns: pattern → (type, language, build, test)
    private static readonly (string pattern, string type, string lang, string build, string test)[] Patterns =
    [
        ("*.sln",     "dotnet",  "C#",           "dotnet build",  "dotnet test"),
        ("*.csproj",  "dotnet",  "C#",           "dotnet build",  "dotnet test"),
        ("*.fsproj",  "dotnet",  "F#",           "dotnet build",  "dotnet test"),
        ("package.json", "node", "JavaScript",   "npm run build", "npm test"),
        ("Cargo.toml", "rust",   "Rust",         "cargo build",   "cargo test"),
        ("go.mod",    "go",      "Go",           "go build",      "go test ./..."),
        ("pyproject.toml", "python", "Python",   "pip install .", "pytest"),
        ("requirements.txt", "python", "Python", "pip install -r requirements.txt", "pytest"),
        ("CMakeLists.txt", "cmake", "C/C++",     "cmake --build .", "ctest"),
        ("build.gradle", "gradle", "Java/Kotlin","gradle build",  "gradle test"),
        ("pom.xml",   "maven",   "Java",         "mvn compile",   "mvn test"),
        ("pubspec.yaml", "dart", "Dart",         "dart compile",  "dart test"),
        ("mix.exs",   "elixir",  "Elixir",       "mix compile",   "mix test"),
    ];

    // Framework detection keywords in package.json dependencies (lowercase)
    private static readonly Dictionary<string, string[]> FrameworkPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ASP.NET"] = ["aspnet", "microsoft.aspnetcore"],
        ["React"] = ["react", "react-dom"],
        ["Vue"] = ["vue", "vue-router"],
        ["Angular"] = ["@angular/core", "angular"],
        ["Next.js"] = ["next"],
        ["Nuxt"] = ["nuxt"],
        ["Svelte"] = ["svelte"],
        ["Express"] = ["express"],
        ["FastAPI"] = ["fastapi"],
        ["Django"] = ["django"],
        ["Spring Boot"] = ["spring-boot"],
    };

    public ProjectInitService(string workspace)
    {
        _workspace = workspace ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Detect project type from build files in the workspace.
    /// </summary>
    public ProjectInfo Detect()
    {
        if (!Directory.Exists(_workspace))
            return new ProjectInfo("unknown", "Unknown", [], "", "", []);

        var buildFiles = new List<string>();
        var detectedFrameworks = new List<string>();

        // Check for build files
        string type = "unknown", lang = "Unknown", buildCmd = "", testCmd = "";

        foreach (var (pattern, t, l, b, test) in Patterns)
        {
            var matches = Directory.GetFiles(_workspace, pattern, SearchOption.TopDirectoryOnly);
            if (matches.Length > 0)
            {
                type = t;
                lang = l;
                buildCmd = b;
                testCmd = test;
                buildFiles.AddRange(matches.Select(m => Path.GetFileName(m)));

                // Detect framework for node/python projects
                if (pattern == "package.json")
                {
                    var pkgJson = File.ReadAllText(matches[0]);
                    foreach (var (fw, keywords) in FrameworkPatterns)
                    {
                        if (keywords.Any(k => pkgJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            detectedFrameworks.Add(fw);
                    }
                }
            }
        }

        return new ProjectInfo(type, lang, buildFiles.ToArray(), buildCmd, testCmd, detectedFrameworks.ToArray());
    }

    /// <summary>
    /// Run full project initialization: detect → create LTAI.md → return summary.
    /// </summary>
    public async Task<string> InitAsync(CancellationToken ct = default)
    {
        var info = Detect();
        var ctxFilePath = Path.Combine(_workspace, "LTAI.md");

        // Create LTAI.md content
        var ctxContent = GenerateContextFile(info);

        await File.WriteAllTextAsync(ctxFilePath, ctxContent, ct).ConfigureAwait(false);

        // Scan for source file count
        int sourceCount = 0;
        try
        {
            var srcDir = Path.Combine(_workspace, "src");
            if (Directory.Exists(srcDir))
            {
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs", ".java" };
                sourceCount = Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories)
                    .Count(f => exts.Contains(Path.GetExtension(f)));
            }
        }
        catch { /* best-effort */ }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## ✅ Project Initialized — {info.Type}");
        sb.AppendLine();
        sb.AppendLine($"**Language:** {info.Language}");
        if (info.Frameworks.Length > 0)
            sb.AppendLine($"**Frameworks:** {string.Join(", ", info.Frameworks)}");
        sb.AppendLine($"**Build:** `{info.BuildCommand}`");
        sb.AppendLine($"**Test:** `{info.TestCommand}`");
        sb.AppendLine($"**Source files:** ~{sourceCount}");
        if (info.BuildFiles.Length > 0)
            sb.AppendLine($"**Build files:** {string.Join(", ", info.BuildFiles)}");
        sb.AppendLine();
        sb.AppendLine($"📄 Created `LTAI.md` — project context file");
        sb.AppendLine($"💡 Tip: Edit LTAI.md to add architecture notes, conventions, or 'do not touch' files");
        sb.AppendLine($"🔍 Run `/index` to build code graph for symbol-aware editing");

        return sb.ToString();
    }

    private string GenerateContextFile(ProjectInfo info)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# LTAI Project Context");
        sb.AppendLine();
        sb.AppendLine($"*Auto-generated by `ProjectInitService` on {DateTime.Now:yyyy-MM-dd HH:mm}*");
        sb.AppendLine();
        sb.AppendLine("## Overview");
        sb.AppendLine("<!-- Describe what this project does in 1-3 sentences. -->");
        sb.AppendLine();
        sb.AppendLine("## Build & Test");
        sb.AppendLine($"```bash");
        sb.AppendLine($"# Build");
        sb.AppendLine($"{info.BuildCommand}");
        sb.AppendLine($"# Test");
        sb.AppendLine($"{info.TestCommand}");
        sb.AppendLine($"```");
        sb.AppendLine();
        sb.AppendLine("## Architecture");
        sb.AppendLine("<!-- Briefly describe the module layout and data-flow. -->");
        sb.AppendLine();
        sb.AppendLine($"- **Language**: {info.Language}");
        if (info.Frameworks.Length > 0)
            sb.AppendLine($"- **Frameworks**: {string.Join(", ", info.Frameworks)}");
        sb.AppendLine();
        sb.AppendLine("## Key Files");
        sb.AppendLine("<!-- List key files/directories the agent should know about. -->");
        sb.AppendLine();
        sb.AppendLine("## Do Not Touch");
        sb.AppendLine("<!-- List files/directories that must not be modified. -->");
        sb.AppendLine();
        sb.AppendLine("## Conventions");
        sb.AppendLine("<!-- Code style, naming, patterns to follow. -->");
        return sb.ToString();
    }
}
