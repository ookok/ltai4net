using System.Text.RegularExpressions;
using SysPath = global::System.IO.Path;

namespace LTAI.Core.System;

public sealed class ResolvedPath
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public bool IsNew { get; set; } = true;
    public bool ConflictResolved { get; set; }
}

public sealed class FileResolver
{
    private static readonly Lazy<FileResolver> _instance = new(() => new FileResolver("."));
    public static FileResolver Instance => _instance.Value;

    private static readonly Dictionary<string, string> OutputRules = new()
    {
        [".py"] = "src", [".js"] = "src", [".ts"] = "src", [".cs"] = "src",
        [".go"] = "src", [".rs"] = "src", [".java"] = "src",
        [".docx"] = "output", [".pdf"] = "output", [".xlsx"] = "output",
        [".md"] = "docs", [".txt"] = "output",
        [".json"] = "data", [".yaml"] = "data", [".yml"] = "data", [".toml"] = "data",
        [".csv"] = "data", [".html"] = "web", [".css"] = "web",
        [".png"] = "assets", [".jpg"] = "assets", [".svg"] = "assets",
        [".sh"] = "scripts", [".bat"] = "scripts", [".ps1"] = "scripts"
    };

    private static readonly string[] ProjectMarkers = { ".git", "pyproject.toml", "package.json", "LTAI.sln", "*.csproj" };

    private readonly string _projectRoot;

    private FileResolver(string workspace)
    {
        _projectRoot = DetectRoot(workspace);
    }

    public string ProjectRoot => _projectRoot;

    private static string DetectRoot(string workspace)
    {
        var current = SysPath.GetFullPath(workspace);
        for (var i = 0; i < 5; i++)
        {
            foreach (var marker in ProjectMarkers)
            {
                if (marker.Contains('*'))
                {
                    if (global::System.IO.Directory.GetFiles(current, marker).Length > 0)
                        return current;
                }
                else if (global::System.IO.File.Exists(SysPath.Combine(current, marker)) ||
                         global::System.IO.Directory.Exists(SysPath.Combine(current, marker)))
                {
                    return current;
                }
            }
            var parent = global::System.IO.Directory.GetParent(current);
            if (parent == null || parent.FullName == current) break;
            current = parent.FullName;
        }
        return SysPath.GetFullPath(workspace);
    }

    public ResolvedPath Resolve(string filename, string directory = "", bool autoRename = true)
    {
        var ext = SysPath.GetExtension(filename).ToLower();
        var baseDir = directory.Length > 0
            ? SysPath.Combine(_projectRoot, directory)
            : SysPath.Combine(_projectRoot, OutputRules.GetValueOrDefault(ext, ""));

        if (!global::System.IO.Directory.Exists(baseDir))
            global::System.IO.Directory.CreateDirectory(baseDir);

        var path = SysPath.Combine(baseDir, filename);
        var result = new ResolvedPath { Path = path, Exists = global::System.IO.File.Exists(path) };

        if (result.Exists && autoRename)
        {
            var stem = SysPath.GetFileNameWithoutExtension(filename);
            for (var i = 1; i < 10; i++)
            {
                var newName = $"{stem}_{i}{ext}";
                var newPath = SysPath.Combine(baseDir, newName);
                if (!global::System.IO.File.Exists(newPath))
                {
                    result.Path = newPath;
                    result.ConflictResolved = true;
                    result.IsNew = true;
                    break;
                }
            }
        }

        return result;
    }

    public ResolvedPath ResolveForContent(string content, string prefix = "output", string ext = ".md")
    {
        var detectedDir = ext switch
        {
            ".cs" or ".py" or ".js" or ".ts" or ".go" or ".rs" => "src",
            ".md" or ".rst" => "docs",
            ".json" or ".yaml" or ".csv" => "data",
            _ => "output"
        };

        if (Regex.IsMatch(content, @"^(using |import |func |class |public class)", RegexOptions.Multiline))
        {
            detectedDir = "src";
            ext = ".cs";
        }
        else if (Regex.IsMatch(content, @"^#+", RegexOptions.Multiline))
        {
            detectedDir = "docs";
            ext = ".md";
        }
        else if (content.TrimStart().StartsWith('{') || content.TrimStart().StartsWith('['))
        {
            detectedDir = "data";
            ext = ".json";
        }

        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var name = $"{prefix}_{ts}{ext}";
        return Resolve(name, detectedDir);
    }

    public string Write(ResolvedPath resolved, string content)
    {
        var dir = SysPath.GetDirectoryName(resolved.Path);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);
        global::System.IO.File.WriteAllText(resolved.Path, content);
        return Relative(resolved.Path);
    }

    private string Relative(string path)
    {
        try
        {
            return SysPath.GetRelativePath(_projectRoot, path);
        }
        catch
        {
            return path;
        }
    }
}
