using System.Collections.Concurrent;
using LTAI.Core.System;
using Microsoft.Data.Sqlite;

namespace LTAI.Tools.CodeGraph;

public sealed class CallGraphNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Line { get; set; }
    public int EndLine { get; set; }
    public string? ParentClass { get; set; }
    public string? Route { get; set; }
    public int CallerCount { get; set; }
    public int CalleeCount { get; set; }
    public int ImportCount { get; set; }
    public string? SourceCode { get; set; }
    public double Complexity { get; set; }
    public ulong Fingerprint { get; set; }
}

public sealed class CallGraphEdge
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Relation { get; set; } = "calls";
    public int Line { get; set; }
}

public sealed class ImpactResult
{
    public string TargetSymbol { get; set; } = "";
    public int Radius { get; set; }
    public int DirectCallers { get; set; }
    public int TransitiveCallers { get; set; }
    public int AffectedFiles { get; set; }
    public int AffectedTests { get; set; }
    public List<CallGraphNode> AffectedNodes { get; set; } = new();
}

/// <summary>
/// 分析轨迹记录 (用于多步分析的上下文累积)
/// </summary>
public sealed record AnalysisTrajectory
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Query { get; init; } = "";
    public string Action { get; init; } = "";
    public List<string> Findings { get; init; } = new();
    public ulong ContextFingerprint { get; init; }
}

public sealed class CodeGraphEnhanced : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _rootDir;
    private readonly LanguageParserRegistry _parserRegistry;
    private readonly ConcurrentDictionary<string, CallGraphNode> _nodes = new();
    private readonly List<CallGraphEdge> _edges = new();
    private readonly List<AnalysisTrajectory> _trajectories = new();
    private readonly object _lock = new();
    private int _totalFiles, _totalNodes, _totalEdges;
    private readonly HashSet<string> _languages = new();

    public CodeGraphEnhanced(DataPathResolver dataPath, string? rootDir = null)
    {
        _rootDir = rootDir ?? Directory.GetCurrentDirectory();
        _parserRegistry = new LanguageParserRegistry();
        var dbPath = dataPath.GetPath("codegraph.db");
        var dir = global::System.IO.Path.GetDirectoryName(dbPath);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS nodes (
                id TEXT PRIMARY KEY, name TEXT, file TEXT, kind TEXT, line INTEGER,
                end_line INTEGER, parent_class TEXT, route TEXT, source_code TEXT, complexity REAL, fingerprint INTEGER);
            CREATE TABLE IF NOT EXISTS edges (
                source_id TEXT, target_id TEXT, relation TEXT, line INTEGER,
                PRIMARY KEY (source_id, target_id, relation));
            CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(name, file, kind, source_code, content=nodes, content_rowid=rowid);
            CREATE INDEX IF NOT EXISTS idx_edges_source ON edges(source_id);
            CREATE INDEX IF NOT EXISTS idx_edges_target ON edges(target_id);
            CREATE INDEX IF NOT EXISTS idx_nodes_file ON nodes(file);
            CREATE INDEX IF NOT EXISTS idx_nodes_fingerprint ON nodes(fingerprint);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task IndexAsync(Action<int, int>? onProgress = null)
    {
        var excludes = new[] { "\\obj\\", "\\bin\\", "\\node_modules\\", "\\.git\\", "\\dist\\", "\\build\\", "\\target\\", "\\vendor\\", "third_party" };

        // 1. 智能预扫描：根据文件占比自动决定索引哪些语言 (默认阈值 1%)
        var activeExtensions = _parserRegistry.GetActiveExtensions(_rootDir, threshold: 0.01);
        
        if (activeExtensions.Count == 0)
        {
            return;
        }

        // 2. 仅收集活跃语言的文件
        var files = new List<string>();
        foreach (var ext in activeExtensions)
        {
            try
            {
                files.AddRange(Directory.GetFiles(_rootDir, $"*{ext}", SearchOption.AllDirectories)
                    .Where(f => !excludes.Any(e => f.Contains(e, StringComparison.OrdinalIgnoreCase))));
            }
            catch (UnauthorizedAccessException) { }
        }

        _totalFiles = files.Count;
        var processed = 0;

        // 3. 并行解析
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (file, ct) =>
        {
            await Task.Run(() =>
            {
                try { ParseFileMultiLang(file); }
                catch { /* non-fatal */ }
                var done = Interlocked.Increment(ref processed);
                onProgress?.Invoke(done, _totalFiles);
            }, ct);
        });

        CommitToDb();
        _totalNodes = _nodes.Count;
        _totalEdges = _edges.Count;
        RebuildFts();
    }

    private void ParseFileMultiLang(string file)
    {
        if (!global::System.IO.File.Exists(file)) return;
        var content = global::System.IO.File.ReadAllText(file);
        var parser = _parserRegistry.GetParserForFile(file);
        if (parser == null) return;

        _languages.Add(parser.LanguageName);
        var symbols = parser.ParseFileAsync(file, content).GetAwaiter().GetResult();
        var relativePath = global::System.IO.Path.GetRelativePath(_rootDir, file).Replace('\\', '/');

        foreach (var symbol in symbols)
        {
            var fingerprint = SimHash.Compute(symbol.SourceCode ?? $"{symbol.Name}{symbol.ParentClass}");
            AddNode(symbol.Name, relativePath, symbol.Kind.ToString().ToLower(), symbol.Line, symbol.EndLine, symbol.ParentClass, fingerprint);
            _nodes.TryGetValue($"{relativePath}:{symbol.Name}:{symbol.Line}", out var node);
            if (node != null)
            {
                node.Complexity = symbol.Complexity;
                node.SourceCode = symbol.SourceCode;
                node.Fingerprint = fingerprint;
            }

            foreach (var dep in symbol.Dependencies)
            {
                lock (_lock)
                {
                    _edges.Add(new CallGraphEdge
                    {
                        SourceId = $"{relativePath}:{symbol.Name}:{symbol.Line}",
                        TargetId = dep,
                        Relation = "calls",
                        Line = symbol.Line
                    });
                }
            }
        }
    }

    private void ParseFile(string file)
    {
        if (!global::System.IO.File.Exists(file)) return;
        var content = global::System.IO.File.ReadAllText(file);
        var relativePath = global::System.IO.Path.GetRelativePath(_rootDir, file).Replace('\\', '/');

        var lines = content.Split('\n');
        var classStack = new Stack<(string name, int line)>();
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var stripped = line.TrimStart();

            if (stripped.StartsWith("class ") || stripped.StartsWith("public class ") || stripped.StartsWith("internal class "))
            {
                var nameMatch = System.Text.RegularExpressions.Regex.Match(stripped, @"class\s+(\w+)");
                if (nameMatch.Success)
                {
                    var className = nameMatch.Groups[1].Value;
                    classStack.Push((className, i + 1));
                    currentClass = string.Join(".", classStack.Select(c => c.name));
                    AddNode(className, relativePath, "class", i + 1, i + 1, currentClass);

                    var routeMatch = System.Text.RegularExpressions.Regex.Match(content, $@"\[HttpGet\(""([^""]+)""\)\]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
                    if (!routeMatch.Success)
                        routeMatch = System.Text.RegularExpressions.Regex.Match(content, $@"\[HttpPost\(""([^""]+)""\)\]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
                    if (!routeMatch.Success)
                        routeMatch = System.Text.RegularExpressions.Regex.Match(content, $@"\[Route\(""([^""]+)""\)\]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
                    if (routeMatch.Success)
                        SetRoute(className, routeMatch.Groups[1].Value);
                }
            }

            if (stripped.StartsWith("void ") || stripped.StartsWith("public void ") || stripped.StartsWith("private void ") ||
                stripped.StartsWith("async ") || stripped.StartsWith("public async ") ||
                stripped.StartsWith("public static ") || stripped.StartsWith("private static ") ||
                stripped.StartsWith("Task ") || stripped.StartsWith("Task<") || stripped.StartsWith("ValueTask"))
            {
                var nameMatch = System.Text.RegularExpressions.Regex.Match(stripped, @"(?:void|Task\w*|ValueTask\w*)\s+(\w+)\s*[\(<]");
                if (!nameMatch.Success)
                {
                    var wordMatch = System.Text.RegularExpressions.Regex.Match(stripped, @"\s+(\w+)\s*[\(<]");
                    if (wordMatch.Success && wordMatch.Groups[1].Value != "class" && wordMatch.Groups[1].Value != "static")
                        nameMatch = wordMatch;
                }

                if (nameMatch.Success)
                {
                    var methodName = nameMatch.Groups[1].Value;
                    AddNode(methodName, relativePath, "method", i + 1, i + 1, currentClass);

                    var routeMatch = System.Text.RegularExpressions.Regex.Match(content, $@"\[HttpGet\(""([^""]+)""\)\]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
                    if (routeMatch.Success && routeMatch.Groups[1].Value.Contains(methodName, StringComparison.OrdinalIgnoreCase))
                        SetRoute(methodName, routeMatch.Groups[1].Value);
                }

                ExtractCalls(nameMatch.Groups[1].Value, content, i, relativePath, currentClass);
            }
        }
    }

    private void AddNode(string name, string file, string kind, int line, int endLine, string? parent, ulong fingerprint = 0)
    {
        var id = $"{file}:{name}:{line}";
        _nodes.TryAdd(id, new CallGraphNode
        {
            Id = id, Name = name, File = file, Kind = kind,
            Line = line, EndLine = endLine, ParentClass = parent, Fingerprint = fingerprint
        });
    }

    private void SetRoute(string name, string route)
    {
        foreach (var (_, node) in _nodes)
        {
            if (node.Name == name)
                node.Route = route;
        }
    }

    private void ExtractCalls(string callerName, string fullContent, int line, string file, string currentClass)
    {
        var methodCalls = System.Text.RegularExpressions.Regex.Matches(fullContent,
            @"\b(\w+)\.(\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));

        foreach (System.Text.RegularExpressions.Match m in methodCalls)
        {
            var target = m.Groups[2].Value;
            if (target == callerName || target.Length < 2) continue;

            lock (_lock)
            {
                _edges.Add(new CallGraphEdge
                {
                    SourceId = $"{file}:{callerName}:{line}",
                    TargetId = target,
                    Relation = "calls",
                    Line = line
                });
            }
        }
    }

    private void CommitToDb()
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO nodes VALUES (@id, @name, @file, @kind, @line, @end_line, @parent_class, @route, @source_code, @complexity, @fingerprint)";
        var idP = cmd.Parameters.Add("@id", SqliteType.Text);
        var nameP = cmd.Parameters.Add("@name", SqliteType.Text);
        var fileP = cmd.Parameters.Add("@file", SqliteType.Text);
        var kindP = cmd.Parameters.Add("@kind", SqliteType.Text);
        var lineP = cmd.Parameters.Add("@line", SqliteType.Integer);
        var endP = cmd.Parameters.Add("@end_line", SqliteType.Integer);
        var parentP = cmd.Parameters.Add("@parent_class", SqliteType.Text);
        var routeP = cmd.Parameters.Add("@route", SqliteType.Text);
        var srcP = cmd.Parameters.Add("@source_code", SqliteType.Text);
        var compP = cmd.Parameters.Add("@complexity", SqliteType.Real);
        var fpP = cmd.Parameters.Add("@fingerprint", SqliteType.Integer);

        foreach (var (_, node) in _nodes)
        {
            idP.Value = node.Id; nameP.Value = node.Name; fileP.Value = node.File;
            kindP.Value = node.Kind; lineP.Value = node.Line; endP.Value = node.EndLine;
            parentP.Value = (object?)node.ParentClass ?? DBNull.Value;
            routeP.Value = (object?)node.Route ?? DBNull.Value;
            srcP.Value = (object?)node.SourceCode ?? DBNull.Value;
            compP.Value = node.Complexity;
            fpP.Value = (long)node.Fingerprint;
            cmd.ExecuteNonQuery();
        }

        using var edgeCmd = _db.CreateCommand();
        edgeCmd.CommandText = "INSERT OR REPLACE INTO edges VALUES (@sid, @tid, @rel, @line)";
        var sidP = edgeCmd.Parameters.Add("@sid", SqliteType.Text);
        var tidP = edgeCmd.Parameters.Add("@tid", SqliteType.Text);
        var relP = edgeCmd.Parameters.Add("@rel", SqliteType.Text);
        var elineP = edgeCmd.Parameters.Add("@line", SqliteType.Integer);

        foreach (var edge in _edges)
        {
            sidP.Value = edge.SourceId; tidP.Value = edge.TargetId;
            relP.Value = edge.Relation; elineP.Value = edge.Line;
            edgeCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void RebuildFts()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO nodes_fts(nodes_fts) VALUES('rebuild')";
        cmd.ExecuteNonQuery();
    }

    public List<CallGraphNode> Search(string query, string? kind = null, int limit = 20)
    {
        var results = new List<CallGraphNode>();
        var sql = "SELECT n.* FROM nodes n JOIN nodes_fts f ON n.rowid = f.rowid WHERE nodes_fts MATCH @query";
        if (!string.IsNullOrEmpty(kind)) sql += " AND n.kind = @kind";
        sql += " ORDER BY rank LIMIT @limit";

        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@query", $"\"{query}\"");
        if (!string.IsNullOrEmpty(kind)) cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadNode(reader));
        return results;
    }

    public List<CallGraphNode> GetCallers(string symbolId, int depth = 1)
    {
        var visited = new HashSet<string>();
        return GetCallersRecursive(symbolId, depth, visited);
    }

    private List<CallGraphNode> GetCallersRecursive(string symbolId, int depth, HashSet<string> visited)
    {
        var results = new List<CallGraphNode>();
        if (depth < 0 || !visited.Add(symbolId)) return results;

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT n.* FROM nodes n JOIN edges e ON n.id = e.source_id WHERE e.target_id = @tid";
        cmd.Parameters.AddWithValue("@tid", symbolId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var node = ReadNode(reader);
            results.Add(node);
            node.CallerCount = results.Count;
            if (depth > 0) results.AddRange(GetCallersRecursive(node.Id, depth - 1, visited));
        }
        return results;
    }

    public List<CallGraphNode> GetCallees(string symbolId, int depth = 1)
    {
        var visited = new HashSet<string>();
        return GetCalleesRecursive(symbolId, depth, visited);
    }

    private List<CallGraphNode> GetCalleesRecursive(string symbolId, int depth, HashSet<string> visited)
    {
        var results = new List<CallGraphNode>();
        if (depth < 0 || !visited.Add(symbolId)) return results;

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT n.* FROM nodes n JOIN edges e ON n.id = e.target_id WHERE e.source_id = @sid AND n.id != @sid";
        cmd.Parameters.AddWithValue("@sid", symbolId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var node = ReadNode(reader);
            results.Add(node);
            node.CalleeCount = results.Count;
            if (depth > 0) results.AddRange(GetCalleesRecursive(node.Id, depth - 1, visited));
        }
        return results;
    }

    public ImpactResult GetImpactRadius(string symbolId, int maxDepth = 3)
    {
        var result = new ImpactResult { TargetSymbol = symbolId, Radius = maxDepth };
        var affected = new HashSet<string>();
        var testPatterns = new[] { "Tests", "Test", "Spec" };

        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((symbolId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth || !affected.Add(currentId)) continue;

            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT n.* FROM nodes n JOIN edges e ON n.id = e.source_id WHERE e.target_id = @tid AND n.id != @tid";
            cmd.Parameters.AddWithValue("@tid", currentId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var node = ReadNode(reader);
                result.AffectedNodes.Add(node);
                result.AffectedFiles = result.AffectedNodes.Select(n => n.File).Distinct().Count();
                if (testPatterns.Any(t => node.File.Contains(t)))
                    result.AffectedTests++;
                if (depth < maxDepth)
                    queue.Enqueue((node.Id, depth + 1));
            }
        }

        result.DirectCallers = result.AffectedNodes.Count;
        result.TransitiveCallers = result.AffectedNodes.GroupBy(n => n.File).Count() + result.AffectedTests;
        return result;
    }

    public string BuildContext(string task, int maxNodes = 20, string format = "markdown")
    {
        var keywords = System.Text.RegularExpressions.Regex.Matches(task, @"\b\w{3,}\b")
            .Select(m => m.Value.ToLower())
            .Where(w => w is not ("the" or "and" or "for" or "with" or "from" or "that" or "this"))
            .Take(5).ToList();

        var results = new List<CallGraphNode>();
        foreach (var kw in keywords)
        {
            var matches = Search(kw, limit: maxNodes);
            results.AddRange(matches);
        }

        var unique = results.DistinctBy(n => n.Id).Take(maxNodes).ToList();

        if (format == "markdown")
        {
            var sb = new global::System.Text.StringBuilder();
            sb.AppendLine($"## CodeGraph Context: {task}");
            sb.AppendLine($"Found {unique.Count} relevant symbols\n");
            foreach (var n in unique)
            {
                sb.AppendLine($"### {n.Kind}: `{n.Name}` ({n.File}:{n.Line})");
                if (!string.IsNullOrEmpty(n.Route))
                    sb.AppendLine($"Route: `{n.Route}`");
                if (!string.IsNullOrEmpty(n.ParentClass))
                    sb.AppendLine($"Class: `{n.ParentClass}`");
                if (!string.IsNullOrEmpty(n.SourceCode))
                    sb.AppendLine($"\n```csharp\n{n.SourceCode[..Math.Min(500, n.SourceCode.Length)]}\n```");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        return string.Join("\n", unique.Select(n => $"{n.Kind}:{n.Name} @ {n.File}:{n.Line}" + (n.Route != null ? $" [{n.Route}]" : "")));
    }

    public Dictionary<string, object> GetStatus()
    {
        var langDist = _parserRegistry.GetPrimaryLanguages(_rootDir, threshold: 0.01)
            .Select(x => new { language = x.Language, percentage = Math.Round(x.Percentage * 100, 1), files = x.Files })
            .ToList();

        return new()
        {
            ["files_indexed"] = _totalFiles,
            ["total_nodes"] = _totalNodes,
            ["total_edges"] = _totalEdges,
            ["languages"] = _languages.ToList(),
            ["language_distribution"] = langDist,
            ["dominant_language"] = _parserRegistry.GetDominantLanguage(_rootDir) ?? "unknown",
            ["root_dir"] = _rootDir,
            ["database_path"] = _db.DataSource
        };
    }

    public List<CallGraphNode> SearchSimilarCode(ulong fingerprint, int maxDistance = 5, int limit = 10)
    {
        var results = new List<CallGraphNode>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM nodes WHERE ABS(fingerprint - @fp) < @threshold LIMIT @limit";
        var fpParam = cmd.Parameters.Add("@fp", SqliteType.Integer);
        var threshParam = cmd.Parameters.Add("@threshold", SqliteType.Integer);
        var limitParam = cmd.Parameters.Add("@limit", SqliteType.Integer);
        
        fpParam.Value = (long)fingerprint;
        threshParam.Value = (long)Math.Pow(2, maxDistance);
        limitParam.Value = limit;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var node = ReadNode(reader);
            var dist = SimHash.Distance(fingerprint, node.Fingerprint);
            if (dist <= maxDistance)
                results.Add(node);
        }
        return results;
    }

    public List<CallGraphNode> GetAllNodes()
    {
        var results = new List<CallGraphNode>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM nodes";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadNode(reader));
        return results;
    }

    public List<CallGraphEdge> GetAllEdges()
    {
        var results = new List<CallGraphEdge>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT source_id, target_id, relation, line FROM edges";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new CallGraphEdge
            {
                SourceId = reader.GetString(0),
                TargetId = reader.GetString(1),
                Relation = reader.GetString(2),
                Line = reader.GetInt32(3)
            });
        return results;
    }

    private static CallGraphNode ReadNode(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        File = reader.GetString(2),
        Kind = reader.GetString(3),
        Line = reader.GetInt32(4),
        EndLine = reader.GetInt32(5),
        ParentClass = reader.IsDBNull(6) ? null : reader.GetString(6),
        Route = reader.IsDBNull(7) ? null : reader.GetString(7),
        SourceCode = reader.IsDBNull(8) ? null : reader.GetString(8),
        Complexity = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
        Fingerprint = reader.IsDBNull(10) ? 0 : (ulong)reader.GetInt64(10)
    };

    public ImpactResult BlastRadius(string entityName, int maxDepth = 2)
    {
        var results = Search(entityName, limit: 1);
        if (results.Count == 0) return new ImpactResult { TargetSymbol = entityName };
        return GetImpactRadius(results[0].Id, maxDepth);
    }

    public List<CallGraphNode> FindHubs(int topN = 10)
    {
        var results = new List<CallGraphNode>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT n.*, COALESCE(s.cnt, 0) + COALESCE(t.cnt, 0) AS degree FROM nodes n
            LEFT JOIN (SELECT source_id, COUNT(*) AS cnt FROM edges GROUP BY source_id) s ON n.id = s.source_id
            LEFT JOIN (SELECT target_id, COUNT(*) AS cnt FROM edges GROUP BY target_id) t ON n.id = t.target_id
            ORDER BY degree DESC LIMIT @topN
            """;
        cmd.Parameters.AddWithValue("@topN", topN);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadNode(reader));
        return results;
    }

    public Dictionary<string, object> Stats() => new()
    {
        ["files_indexed"] = _totalFiles,
        ["total_nodes"] = _totalNodes,
        ["total_edges"] = _totalEdges,
        ["languages"] = _languages.ToList(),
        ["hubs"] = FindHubs(5).Select(h => new { h.Name, h.File }).ToList()
    };

    public void Dispose() => _db?.Dispose();

    /// <summary>
    /// 累积分析上下文 (Trajectory-Accumulative Conditioning)
    /// 将每次分析的结果存入轨迹，供后续多步推理使用
    /// </summary>
    public void AccumulateContext(string query, string action, List<string> findings)
    {
        var fp = SimHash.Compute(string.Join(" ", findings));
        lock (_lock)
        {
            _trajectories.Add(new AnalysisTrajectory
            {
                Query = query,
                Action = action,
                Findings = findings,
                ContextFingerprint = fp
            });
            if (_trajectories.Count > 100)
                _trajectories.RemoveRange(0, _trajectories.Count - 100);
        }
    }

    /// <summary>
    /// 获取最近的分析历史 (用于构建长上下文 Prompt)
    /// </summary>
    public List<AnalysisTrajectory> GetAnalysisHistory(int lastN = 5)
    {
        lock (_lock)
        {
            return _trajectories.TakeLast(lastN).ToList();
        }
    }

    /// <summary>
    /// 基于历史轨迹的相似性搜索 (查找之前的类似分析)
    /// </summary>
    public List<AnalysisTrajectory> FindSimilarHistory(ulong queryFingerprint, int maxDistance = 10, int limit = 3)
    {
        lock (_lock)
        {
            return _trajectories
                .Where(t => SimHash.Distance(queryFingerprint, t.ContextFingerprint) <= maxDistance)
                .OrderBy(t => SimHash.Distance(queryFingerprint, t.ContextFingerprint))
                .Take(limit)
                .ToList();
        }
    }
}
