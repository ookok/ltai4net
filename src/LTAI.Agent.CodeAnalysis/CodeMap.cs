// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CodeMap — lightweight compact file outline tool
//
//  Inspired by zap-coding-agent's `code_map` tool. Provides a
//  token-efficient structural overview of source files or directories.
//  Uses the existing CodeAnalysisTools.GetSymbols() under the hood
//  but reformats output in a compact GCX1-inspired format.
//
//  Output format (per file):
//    📄 path/to/file.cs
//     c ClassName
//     m MethodName
//     p PropertyName
//     i InterfaceName
//
//  This format is ~40% more token-efficient than the default output.
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LTAI.Agent.CodeAnalysis;

/// <summary>
/// Compact file outline utility. Provides token-efficient structural
/// overviews of source files and directories.
/// </summary>
public static class CodeMap
{
    /// <summary>
    /// Get a compact structural outline of a file or directory.
    /// Returns a token-efficient format suitable for LLM context.
    /// </summary>
    public static async Task<string> GetMapAsync(string workspace, string path, int maxFiles = 20)
    {
        var fp = SafeResolvePath(workspace, path);
        if (fp == null) return "⚠ Path escape detected";
        if (Directory.Exists(fp))
            return await GetDirectoryMapAsync(fp, workspace, maxFiles).ConfigureAwait(false);
        if (File.Exists(fp))
            return await GetFileMapAsync(fp, workspace).ConfigureAwait(false);
        return "⚠ Path not found";
    }

    private static async Task<string> GetFileMapAsync(string filePath, string workspace)
    {
        var rel = GetRelativePath(workspace, filePath);
        var ext = Path.GetExtension(filePath);
        var tools = new CodeAnalysisTools(workspace);

        var symbols = await tools.GetSymbols(filePath).ConfigureAwait(false);
        if (symbols == "File not found" || symbols.StartsWith("Error"))
            return $"⚠ {symbols}";

        return CompactFormat(rel, symbols);
    }

    private static async Task<string> GetDirectoryMapAsync(string dirPath, string workspace, int maxFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📁 {GetRelativePath(workspace, dirPath)}/");
        sb.AppendLine();

        var allExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs", ".java",
              ".sh", ".bash", ".json", ".html", ".css", ".yaml", ".yml" };

        var files = new List<string>();
        try
        {
            foreach (var ext in allExts)
            {
                foreach (var f in Directory.EnumerateFiles(dirPath, "*" + ext, SearchOption.AllDirectories))
                {
                    var rel = GetRelativePath(workspace, f);
                    if (!ShouldSkip(rel))
                        files.Add(f);
                }
            }
        }
        catch { /* permission denied — skip */ }

        // Sort: shortest path first
        files.Sort((a, b) => a.Length.CompareTo(b.Length));

        if (files.Count == 0)
        {
            sb.AppendLine("(no source files found)");
            return sb.ToString();
        }

        if (files.Count > maxFiles)
        {
            sb.AppendLine($"_{files.Count} source files total, showing first {maxFiles}_");
            sb.AppendLine();
            files = files.Take(maxFiles).ToList();
        }

        var tools = new CodeAnalysisTools(workspace);
        var tasks = files.Select(f => ProcessFileAsync(f, workspace, tools));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var r in results.Where(r => r != null))
            sb.Append(r);

        return sb.ToString();
    }

    private static async Task<string?> ProcessFileAsync(string filePath, string workspace, CodeAnalysisTools tools)
    {
        try
        {
            var rel = GetRelativePath(workspace, filePath);
            var symbols = await tools.GetSymbols(filePath).ConfigureAwait(false);
            if (symbols == "File not found" || symbols.StartsWith("Error") || symbols == "No symbols found.")
                return null;

            return CompactFormat(rel, symbols);
        }
        catch
        {
            return null;
        }
    }

    private static string CompactFormat(string relPath, string symbolsOutput)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📄 {relPath}");

        // Parse existing format and re-output compactly
        foreach (var line in symbolsOutput.Split('\n'))
        {
            // Match lines like "  L   42  class      MyClass"
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            // parts[0] = L{N}, parts[1] = kind, parts[2+] = name
            var lineNum = parts[0].TrimStart('L');
            var kind = GetShortKind(parts[1]);
            var name = string.Join(" ", parts[2..]);

            sb.AppendLine($"  {kind} {name}  L{lineNum}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string GetShortKind(string kind) => kind.ToLowerInvariant() switch
    {
        "class" => "c",
        "struct" => "s",
        "interface" => "i",
        "record" => "r",
        "method" => "m",
        "property" => "p",
        "enum" => "e",
        "program" => "x",
        "function" => "fn",
        "variable" => "v",
        "const" or "constant" => "k",
        "type" or "typedef" => "t",
        _ => kind[..Math.Min(3, kind.Length)]
    };

    private static string GetRelativePath(string workspace, string fullPath)
    {
        try { return Path.GetRelativePath(workspace, fullPath).Replace('\\', '/'); }
        catch { return fullPath.Replace('\\', '/'); }
    }

    private static bool ShouldSkip(string relPath)
    {
        if (relPath.StartsWith("bin/", StringComparison.Ordinal) ||
            relPath.StartsWith("obj/", StringComparison.Ordinal) ||
            relPath.StartsWith("node_modules/", StringComparison.Ordinal) ||
            relPath.StartsWith(".git/", StringComparison.Ordinal) ||
            relPath.StartsWith(".vs/", StringComparison.Ordinal) ||
            relPath.StartsWith(".livingtree/", StringComparison.Ordinal) ||
            relPath.Contains("/bin/") || relPath.Contains("/obj/"))
            return true;
        return false;
    }

    private static string? SafeResolvePath(string workspace, string path)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(workspace, path));
            if (!full.StartsWith(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
        catch { return null; }
    }
}
