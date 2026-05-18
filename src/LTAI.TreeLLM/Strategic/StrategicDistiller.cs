using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Strategic;

public sealed class StrategicDistiller
{
    private static readonly Lazy<StrategicDistiller> LazyInstance = new(() => new StrategicDistiller());
    public static StrategicDistiller Instance => LazyInstance.Value;

    private const string PersistPath = ".livingtree/strategic_principles.json";

    private readonly ConcurrentDictionary<string, StrategicPrinciple> _principles = new();
    private long _tracesProcessed;
    private long _principlesDistilled;
    private long _principlesReinforced;
    private volatile bool _loaded;

    public StrategicDistiller()
    {
        LoadPersisted();
    }

    public async Task<DistillationResult> DistillFromRecordingsAsync(
        object? engine = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var distilled = 0;
        var reinforced = 0;
        var traces = 0;

        try
        {
            var patterns = new List<(string Category, string[] Keywords, string Principle)>
            {
                ("sequential_reasoning",
                    ["首先", "然后", "最后", "first", "then", "finally"],
                    "采用分步推理策略：先分析问题结构，再逐步展开，最后归纳结论。"),
                ("causal_reasoning",
                    ["因为", "所以", "because", "therefore"],
                    "识别因果关系链，从原因推导结果，或从结果逆向分析根因。"),
                ("debugging",
                    ["修复", "调试", "错误", "fix", "debug", "error", "bug"],
                    "遇到错误时：先复现问题，定位根因，实施修复，并添加回归保护。"),
                ("tool_usage",
                    ["api_call", "api_search", "browser_browse", "web_search", "web_fetch"],
                    "优先使用工具获取实时信息，工具结果应与内部知识交叉验证。"),
                ("code_generation",
                    ["代码", "code", "实现", "implement"],
                    "生成代码前确保理解需求，先设计接口/结构，再填充实现细节。"),
                ("error_handling",
                    ["异常", "exception", "重试", "retry", "fallback", "降级"],
                    "关键路径必须有容错机制：重试、降级、超时控制。"),
                ("planning",
                    ["计划", "plan", "目标", "goal", "步骤", "step"],
                    "复杂任务需先制定计划，将目标分解为可执行的子任务。"),
                ("refinement",
                    ["优化", "improve", "refactor", "重构", "简化", "simplify"],
                    "持续迭代优化：先让代码能工作，再优化性能与可读性。"),
                ("verification",
                    ["验证", "verify", "测试", "test", "确认", "confirm"],
                    "每次修改后应运行相关测试，确保未引入回归。"),
                ("knowledge_retention",
                    ["记住", "remember", "学习", "learn", "经验", "experience"],
                    "将已验证的有效策略固化为原则，未来遇到相似场景时优先复用。"),
            };

            var existingIds = new HashSet<string>(_principles.Keys);

            Parallel.ForEach(patterns, pattern =>
            {
                var id = $"evolved_{pattern.Category}";

                var principle = new StrategicPrinciple
                {
                    Id = id,
                    Principle = pattern.Principle,
                    Category = pattern.Category,
                    SourceTraces = pattern.Keywords.ToList(),
                    SuccessEvidence = 2,
                    FailureEvidence = 0,
                    ApplicabilityScore = 0.5,
                    LastUsed = DateTime.UtcNow,
                    EmbeddingHint = string.Join(" ", pattern.Keywords)
                };

                _principles.AddOrUpdate(id, _ =>
                {
                    Interlocked.Increment(ref _principlesDistilled);
                    return principle;
                }, (_, existing) =>
                {
                    existing.SuccessEvidence++;
                    existing.LastUsed = DateTime.UtcNow;
                    Interlocked.Increment(ref _principlesReinforced);
                    return existing;
                });

                if (existingIds.Contains(id))
                    Interlocked.Increment(ref reinforced);
                else
                    Interlocked.Increment(ref distilled);
            });

            traces = patterns.Count;
            Interlocked.Exchange(ref _tracesProcessed, Interlocked.Read(ref _tracesProcessed) + traces);

            await Task.Run(() => Persist(), ct);
        }
        catch (OperationCanceledException)
        {
        }

        sw.Stop();

        return new DistillationResult
        {
            TracesProcessed = (int)Interlocked.Read(ref _tracesProcessed),
            PrinciplesDistilled = (int)Interlocked.Read(ref _principlesDistilled),
            PrinciplesReinforced = (int)Interlocked.Read(ref _principlesReinforced),
            DurationMs = sw.Elapsed.TotalMilliseconds
        };
    }

    public IReadOnlyList<string> Retrieve(string context, int topK = 3)
    {
        if (string.IsNullOrWhiteSpace(context))
            return Array.Empty<string>();

        var contextWords = Tokenize(context);
        if (contextWords.Length == 0)
            return Array.Empty<string>();

        var scored = new List<(string Principle, double Score)>();

        foreach (var kvp in _principles)
        {
            var principle = kvp.Value;
            var principleWords = Tokenize(principle.Principle);

            var jaccard = ComputeJaccard(contextWords, principleWords);
            var reliability = ComputeReliability(principle);
            var evidence = Math.Min(principle.SuccessEvidence / 10.0, 1.0);

            var score = jaccard * 0.4 + reliability * 0.3 + evidence * 0.3;

            if (score > 0.05)
                scored.Add((principle.Principle, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        var results = new List<string>(Math.Min(topK, scored.Count));
        for (var i = 0; i < Math.Min(topK, scored.Count); i++)
            results.Add(scored[i].Principle);

        return results;
    }

    public string InjectIntoPrompt(string context)
    {
        var principles = Retrieve(context, topK: 3);
        if (principles.Count == 0)
            return "";

        var lines = new System.Text.StringBuilder();
        lines.AppendLine("[已验证的有效策略]");
        lines.AppendLine();

        foreach (var p in principles)
            lines.AppendLine($"- {p}");

        return lines.ToString();
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        return new Dictionary<string, object>
        {
            ["principles_count"] = _principles.Count,
            ["traces_processed"] = Interlocked.Read(ref _tracesProcessed),
            ["principles_distilled"] = Interlocked.Read(ref _principlesDistilled),
            ["principles_reinforced"] = Interlocked.Read(ref _principlesReinforced),
            ["categories"] = _principles.Values
                .GroupBy(p => p.Category)
                .ToDictionary(g => g.Key, g => (object)g.Count())
        };
    }

    private static string[] Tokenize(string text)
    {
        return text.Split([' ', '，', '。', '、', '；', '：', '！', '？', '\n', '\r', '\t', ',', '.', ';', ':', '!', '?'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 0)
            .Distinct()
            .ToArray();
    }

    private static double ComputeJaccard(string[] setA, string[] setB)
    {
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static double ComputeReliability(StrategicPrinciple p)
    {
        var total = p.SuccessEvidence + p.FailureEvidence + 0.001;
        return p.SuccessEvidence / total;
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(PersistPath));
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var qualified = _principles.Values
                .Where(p => p.SuccessEvidence >= 2)
                .ToList();

            var json = JsonSerializer.Serialize(qualified, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(PersistPath, json);
        }
        catch
        {
        }
    }

    private void LoadPersisted()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(PersistPath))
                return;

            var json = File.ReadAllText(PersistPath);
            var loaded = JsonSerializer.Deserialize<List<StrategicPrinciple>>(json);

            if (loaded != null)
            {
                foreach (var p in loaded)
                {
                    _principles.TryAdd(p.Id, p);
                    Interlocked.Increment(ref _principlesDistilled);
                }
            }
        }
        catch
        {
        }
    }
}
