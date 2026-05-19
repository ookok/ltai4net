using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.CodeGraph;

public enum CodeEntityKind { Function, Class, Module, Import, Interface, Struct }

public record CodeEntity(string Id, string Name, string File, CodeEntityKind Kind, int Line, int EndLine,
    string? ParentClass, List<string> Dependencies, List<string> Dependents,
    double TestCoverage, double Complexity, string Hash);

public sealed class CodeGraph
{
    private readonly ConcurrentDictionary<string, CodeEntity> _entities = new();
    private readonly ConcurrentDictionary<string, List<string>> _fileEntities = new();
    private readonly ILogger<CodeGraph> _logger;
    private readonly string _rootDir;

    public CodeGraph(string? rootDir = null, ILogger<CodeGraph>? logger = null)
    {
        _rootDir = rootDir ?? Directory.GetCurrentDirectory();
        _logger = logger ?? NullLogger<CodeGraph>.Instance;
    }

    public async Task IndexAsync()
    {
        _entities.Clear();
        _fileEntities.Clear();
        var files = Directory.GetFiles(_rootDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        foreach (var file in files)
        {
            await semaphore.WaitAsync();
            _ = Task.Run(() =>
            {
                try { ParseFile(file); }
                finally { semaphore.Release(); }
            });
        }
        for (int i = 0; i < Environment.ProcessorCount; i++) await semaphore.WaitAsync();
        semaphore.Dispose();
        WireDependencies();
        _logger.LogInformation("Indexed {Count} entities from {FileCount} files", _entities.Count, files.Count);
    }

    private void ParseFile(string file)
    {
        var content = File.ReadAllText(file);
        var relativePath = Path.GetRelativePath(_rootDir, file);
        var entities = new List<CodeEntity>();
        var importMatches = Regex.Matches(content, @"^using\s+([\w.]+)", RegexOptions.Multiline);
        foreach (Match m in importMatches)
        {
            var module = m.Groups[1].Value;
            var entity = new CodeEntity(HashId($"{relativePath}:import:{module}"), module, relativePath,
                CodeEntityKind.Import, 0, 0, null, new(), new(), 0, 0, HashContent(module));
            _entities[entity.Id] = entity;
            entities.Add(entity);
        }

        var classMatches = Regex.Matches(content, @"(?:public|internal|static|sealed|partial)\s+class\s+(\w+)");
        foreach (Match m in classMatches)
        {
            var name = m.Groups[1].Value; var line = GetLine(content, m.Index);
            var entity = new CodeEntity(HashId($"{relativePath}:class:{name}"), name, relativePath,
                CodeEntityKind.Class, line, line + 50, null, new(), new(), 0,
                CountBranches(content, m.Index) * 0.5, HashContent(content.Substring(m.Index, Math.Min(500, content.Length - m.Index))));
            _entities[entity.Id] = entity;
            entities.Add(entity);
        }

        var methodMatches = Regex.Matches(content,
            @"(?:public|internal|private|protected|static|async|virtual|override|abstract)\s+(?:[\w<>[\],]+\s+)?(\w+)\s*\(");
        foreach (Match m in methodMatches)
        {
            var name = m.Groups[1].Value; var line = GetLine(content, m.Index);
            if (name is "if" or "while" or "for" or "switch" or "catch" or "lock" or "using") continue;
            var entity = new CodeEntity(HashId($"{relativePath}:method:{name}:{line}"), name, relativePath,
                CodeEntityKind.Function, line, line + 20, null, new(), new(), 0,
                CountBranches(content, m.Index) * 0.3, HashContent(content.Substring(m.Index, Math.Min(300, content.Length - m.Index))));
            _entities[entity.Id] = entity;
            entities.Add(entity);
        }

        if (entities.Count > 0) _fileEntities[relativePath] = entities.Select(e => e.Id).ToList();
    }

    private void WireDependencies()
    {
        foreach (var entity in _entities.Values.Where(e => e.Kind == CodeEntityKind.Import))
        {
            var importedModule = entity.Name;
            foreach (var target in _entities.Values.Where(e => e.File.StartsWith(importedModule.Replace('.', '/')) ||
                e.File.StartsWith(importedModule.Replace('.', '\\'))))
            {
                entity.Dependencies.Add(target.Id);
                target.Dependents.Add(entity.Id);
            }
        }
    }

    public List<CodeEntity> BlastRadius(string entityName, int maxDepth = 2)
    {
        var start = _entities.Values.FirstOrDefault(e => e.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase));
        if (start == null) return new();

        var visited = new Dictionary<string, int>();
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((start.Id, 0));

        while (queue.TryDequeue(out var item))
        {
            if (!visited.ContainsKey(item.Id))
            {
                visited[item.Id] = item.Depth;
                if (item.Depth < maxDepth && _entities.TryGetValue(item.Id, out var entity))
                {
                    foreach (var depId in entity.Dependencies)
                        queue.Enqueue((depId, item.Depth + 1));
                    foreach (var depId in entity.Dependents)
                        queue.Enqueue((depId, item.Depth + 1));
                }
            }
        }

        return visited.Keys.Select(id => _entities.GetValueOrDefault(id)).Where(e => e != null).Cast<CodeEntity>().ToList();
    }

    public List<CodeEntity> GetCallers(string entityName)
        => _entities.Values.Where(e => e.Dependencies.Any(d =>
            _entities.TryGetValue(d, out var dep) && dep.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase))).ToList();

    public List<CodeEntity> GetCallees(string entityName)
        => _entities.Values.Where(e => e.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Dependencies.Select(d => _entities.GetValueOrDefault(d)))
            .Where(e => e != null).Cast<CodeEntity>().ToList();

    public List<CodeEntity> Search(string query)
    {
        var lower = query.ToLowerInvariant();
        return _entities.Values.Where(e => e.Name.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
            e.File.Contains(lower, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public List<CodeEntity> FindHubs(int topN = 10)
        => _entities.Values.OrderByDescending(e => e.Dependencies.Count + e.Dependents.Count).Take(topN).ToList();

    public async Task IncrementalUpdateFromGitAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "diff --name-only")
        {
            WorkingDirectory = _rootDir, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        foreach (var file in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.Combine(_rootDir, file.Trim());
            if (File.Exists(fullPath) && fullPath.EndsWith(".cs"))
            {
                if (_fileEntities.TryGetValue(file.Trim(), out var ids))
                    foreach (var id in ids) _entities.TryRemove(id, out _);
                ParseFile(fullPath);
            }
        }
        WireDependencies();
    }

    public (List<CodeEntity> Added, List<CodeEntity> Removed, List<CodeEntity> Modified) Diff(CodeGraph other)
    {
        var added = other._entities.Values.Where(e => !_entities.ContainsKey(e.Id)).ToList();
        var removed = _entities.Values.Where(e => !other._entities.ContainsKey(e.Id)).ToList();
        var modified = _entities.Values
            .Where(e => other._entities.TryGetValue(e.Id, out var oe) && e.Hash != oe.Hash).ToList();
        return (added, removed, modified);
    }

    public Dictionary<string, object> Stats() => new()
    {
        ["entities"] = _entities.Count, ["files"] = _fileEntities.Count,
        ["functions"] = _entities.Values.Count(e => e.Kind == CodeEntityKind.Function),
        ["classes"] = _entities.Values.Count(e => e.Kind == CodeEntityKind.Class),
        ["imports"] = _entities.Values.Count(e => e.Kind == CodeEntityKind.Import),
        ["hubs"] = FindHubs(5).Select(e => new { e.Name, connections = e.Dependencies.Count + e.Dependents.Count }).ToList()
    };

    private static string HashId(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..12];
    private static string HashContent(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
    private static int GetLine(string text, int index) => text[..index].Count(c => c == '\n') + 1;
    private static double CountBranches(string text, int start)
        => Regex.Matches(text.Substring(start, Math.Min(500, text.Length - start)), @"\b(if|else|for|while|switch|case|catch|when|match|&&|\|\|)\b").Count;
}
