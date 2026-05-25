using LTAI.DNA.Models;

namespace LTAI.DNA.Life;

public sealed class LivingCompiler
{
    private readonly Dictionary<string, CompiledPath> _compiled = new();
    private readonly IntentRecognizer _recognizer = new();
    private readonly object _lock = new();
    private const int MaxCompiled = 1000;

    public async Task<Dictionary<string, object>> Execute(string query, Func<string, Task<Dictionary<string, object>>> hubChat)
    {
        var (intentHash, confidence) = _recognizer.Recognize(query);

        CompiledPath? path;
        lock (_lock) { path = _compiled.GetValueOrDefault(intentHash); }

        if (path != null && !path.IsStale)
        {
            path.LastUsed = DateTime.UtcNow;
            var nativeResult = await ExecuteCompiled(path, query, hubChat).ConfigureAwait(false);
            nativeResult["compile_mode"] = "native";
            nativeResult["intent_confidence"] = confidence;
            return nativeResult;
        }

        var fullResult = await ExecuteFullPipeline(query, hubChat).ConfigureAwait(false);
        CompileFromExecution(intentHash, query, fullResult);
        fullResult["compile_mode"] = "cold";
        fullResult["intent_confidence"] = confidence;
        return fullResult;
    }

    private void CompileFromExecution(string intentHash, string query, Dictionary<string, object> result)
    {
        var toolCalls = new List<string>();
        var knowledgeKeys = new List<string>();

        if (result.TryGetValue("tool_calls", out var tc) && tc is List<string> tcl)
            toolCalls = tcl;
        else if (result.TryGetValue("actions", out var acts) && acts is List<object> al)
            toolCalls = al.Select(a => a.ToString() ?? "").ToList();

        if (result.TryGetValue("knowledge_used", out var ku) && ku is List<string> kul)
            knowledgeKeys = kul;

        var responseTemplate = result.TryGetValue("response", out var r) ? r?.ToString() ?? "" : "";

        var path = new CompiledPath
        {
            IntentHash = intentHash,
            Level = CompileLevel.Hot,
            ToolCalls = toolCalls,
            ResponseTemplate = responseTemplate,
            KnowledgeKeys = knowledgeKeys
        };

        lock (_lock)
        {
            _compiled[intentHash] = path;
            if (_compiled.Count > MaxCompiled)
            {
                var staleKey = _compiled
                    .OrderBy(kv => kv.Value.LastUsed)
                    .First().Key;
                _compiled.Remove(staleKey);
            }
        }
    }

    private async Task<Dictionary<string, object>> ExecuteCompiled(CompiledPath path, string query,
        Func<string, Task<Dictionary<string, object>>> hubChat)
    {
        foreach (var toolCall in path.ToolCalls)
        {
            try { await hubChat(toolCall); }
            catch { /* non-fatal */ }
        }

        if (!string.IsNullOrEmpty(path.ResponseTemplate))
        {
            var response = await hubChat(
                $"Template: {path.ResponseTemplate}\nQuery: {query}\nKnowledge: {string.Join(", ", path.KnowledgeKeys)}");
            path.SuccessCount++;
            return response;
        }

        path.FailureCount++;
        return new() { ["response"] = "Compiled path failed to produce response" };
    }

    private async Task<Dictionary<string, object>> ExecuteFullPipeline(string query,
        Func<string, Task<Dictionary<string, object>>> hubChat)
    {
        return await hubChat(query).ConfigureAwait(false);
    }

    public int RecompileStale()
    {
        lock (_lock)
        {
            var staleKeys = _compiled.Where(kv => kv.Value.IsStale).Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys)
                _compiled.Remove(key);
            return staleKeys.Count;
        }
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["compiled_paths"] = _compiled.Count,
                ["by_level"] = _compiled.Values
                    .GroupBy(p => p.Level)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                ["avg_success_rate"] = _compiled.Values.Count > 0
                    ? _compiled.Values.Average(p => p.SuccessRate)
                    : 0,
                ["stale_count"] = _compiled.Values.Count(p => p.IsStale)
            };
        }
    }
}

public sealed class IntentRecognizer
{
    private static readonly Dictionary<string, string[]> IntentPatterns = new()
    {
        ["code_generation"] = new[] { "write code", "implement", "create function", "add method", "编写", "生成代码" },
        ["code_review"] = new[] { "review code", "code review", "check code", "审查代码", "review" },
        ["debug_error"] = new[] { "fix bug", "debug", "error", "exception", "not working", "修复", "报错" },
        ["knowledge_query"] = new[] { "what is", "how does", "explain", "describe", "是什么", "解释", "说明" },
        ["refactor"] = new[] { "refactor", "restructure", "clean up", "improve code", "重构", "优化代码" },
        ["test_gen"] = new[]
            { "write test", "add test", "generate test", "test case", "写测试", "测试用例" },
        ["config_change"] = new[] { "configure", "setup", "install", "config", "配置", "安装" },
        ["git_ops"] = new[] { "commit", "push", "pull", "merge", "branch", "git" }
    };

    private readonly Dictionary<string, int> _hitCounts = new();

    public (string intentHash, double confidence) Recognize(string query)
    {
        var lower = query.ToLowerInvariant();
        var scores = new Dictionary<string, int>();

        foreach (var (intent, patterns) in IntentPatterns)
        {
            var hits = patterns.Count(p => lower.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (hits > 0)
                scores[intent] = hits;
        }

        if (scores.Count == 0)
        {
            var hash = ComputeHash("general:" + query);
            return (hash, 0.3);
        }

        var best = scores.OrderByDescending(kv => kv.Value).First();
        var confidence = Math.Min(1.0, best.Value / 3.0);
        var intentHash = ComputeHash(best.Key + ":" + query);

        _hitCounts[best.Key] = _hitCounts.GetValueOrDefault(best.Key) + 1;

        return (intentHash, confidence);
    }

    private static string ComputeHash(string input) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(input)))[..16];

    public Dictionary<string, int> GetPatternStats() => new(_hitCounts);
}
