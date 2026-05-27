using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using LTAI.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI.Governors;

public sealed record NeedleToolPrediction
{
    public string ToolName { get; init; } = "";
    public float Confidence { get; init; }
    public int Rank { get; init; }
}

public sealed record NeedleRouteResult
{
    public string Tier { get; init; } = "l0";       // l0, l1, l2
    public List<NeedleToolPrediction> Predictions { get; init; } = new();
    public long InferenceMs { get; init; }
    public string? FallbackReason { get; init; }
    public bool IsLocal { get; init; }

    public bool ShouldEscalate => Tier is "l1" or "l2";
    public bool IsDirectRoute => Tier == "l0" && Predictions.Any(p => p.Confidence >= 0.7f);
}

public sealed class NeedleToolRouter : IDisposable
{
    private readonly ILogger<NeedleToolRouter> _logger;
    private readonly Acceleration.OnnxAccelerator? _accelerator;
    private InferenceSession? _session;
    private readonly int _vocabSize = 30000;
    private readonly int _maxLength = 128;
    private readonly int _numToolCategories;
    private readonly ConcurrentDictionary<string, List<NeedleToolPrediction>> _cache = new();
    private int _maxCacheSize = 2000;
    private bool _isLoaded;
    private readonly SemaphoreSlim _inferLock = new(1, 1);

    private static readonly string[] ToolCategories =
    {
        "web_search", "shell_exec", "filesystem_read", "filesystem_write",
        "git_diff", "git_log", "git_commit", "code_edit", "code_review",
        "datetime_now", "env_sysinfo", "url_fetch", "math_calc",
        "text_translate", "json_format", "csv_parse", "image_gen",
        "knowledge_search", "skill_invoke", "chat", "unknown"
    };

    private static readonly Dictionary<string, string[]> ExpansionRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["web_search"] = new[] { "搜索", "search", "查找", "查询", "百度", "google", "bing" },
        ["shell_exec"] = new[] { "执行", "运行", "run", "命令", "command", "cmd", "bash", "ps", "进程" },
        ["filesystem_read"] = new[] { "文件", "读取", "read", "cat", "查看", "打开", "ls", "dir" },
        ["filesystem_write"] = new[] { "创建", "创建文件", "write", "保存", "save", "新建", "写入" },
        ["git_diff"] = new[] { "diff", "变更", "改动", "修改", "差异", "对比" },
        ["git_log"] = new[] { "log", "历史", "记录", "提交记录", "版本" },
        ["git_commit"] = new[] { "commit", "提交", "推送", "push", "暂存", "stage" },
        ["code_edit"] = new[] { "修改代码", "编辑", "修改", "重构", "refactor", "编辑文件" },
        ["code_review"] = new[] { "审查", "review", "检查代码", "代码检查", "审计" },
        ["datetime_now"] = new[] { "时间", "日期", "date", "time", "现在", "今天", "星期" },
        ["env_sysinfo"] = new[] { "系统", "环境", "内存", "cpu", "磁盘", "操作系统" },
        ["url_fetch"] = new[] { "网址", "url", "fetch", "抓取", "下载", "网页" },
        ["math_calc"] = new[] { "计算", "数学", "math", "求和", "公式", "转换" },
        ["text_translate"] = new[] { "翻译", "translate", "转换语言", "中译", "英译" },
        ["json_format"] = new[] { "json", "格式化", "format", "美化", "prettify" },
        ["csv_parse"] = new[] { "csv", "表格", "数据", "导入", "导出", "excel" },
        ["image_gen"] = new[] { "生成图片", "image", "图片", "画图", "绘图" },
        ["knowledge_search"] = new[] { "知识", "文档", "document", "检索", "查询文档" },
        ["skill_invoke"] = new[] { "skill", "技能", "工具", "能力", "插件" },
        ["chat"] = new[] { "聊天", "对话", "chat", "question", "问答", "你好" }
    };

    private static readonly Dictionary<string, string> ReverseKeywordMap;
    private static readonly HashSet<string> ChatOnlyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "hey", "你好", "thank", "thanks", "谢谢", "bye", "goodbye",
        "what is your name", "你是谁", "who are you"
    };

    static NeedleToolRouter()
    {
        ReverseKeywordMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tool, keywords) in ExpansionRules)
        {
            foreach (var kw in keywords)
                ReverseKeywordMap[kw] = tool;
        }
    }

    public NeedleToolRouter(string? modelPath = null, ILogger<NeedleToolRouter>? logger = null,
        Acceleration.OnnxAccelerator? accelerator = null)
    {
        _logger = logger ?? NullLogger<NeedleToolRouter>.Instance;
        _accelerator = accelerator;
        _numToolCategories = ToolCategories.Length;
        LoadModel(modelPath);
    }

    public bool IsLoaded => _isLoaded;
    public int VocabSize => _vocabSize;

    private void LoadModel(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            _logger.LogInformation("Needle model not found at {Path}, using keyword fallback router", modelPath ?? "null");
            return;
        }

        try
        {
            var options = _accelerator?.CreateSessionOptions() ?? new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1
            };
            _session = new InferenceSession(modelPath, options);

            _isLoaded = true;
            _logger.LogInformation("Needle model loaded: {Size}KB, {Inputs} inputs, {Outputs} outputs",
                new FileInfo(modelPath).Length / 1024,
                _session.InputMetadata.Count,
                _session.OutputMetadata.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Needle model, using keyword fallback");
            _isLoaded = false;
        }
    }

    public async Task<NeedleRouteResult> RouteAsync(
        string query,
        AIToolRegistry? toolRegistry = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (_cache.TryGetValue(query, out var cached) && cached.Count > 0)
        {
            return new NeedleRouteResult
            {
                Tier = DetermineTier(cached),
                Predictions = cached,
                InferenceMs = 0,
                IsLocal = true
            };
        }

        List<NeedleToolPrediction> predictions;

        if (_isLoaded && _session != null)
        {
            predictions = await OnnxPredictAsync(query, ct).ConfigureAwait(false);
        }
        else
        {
            predictions = KeywordPredict(query);
        }

        await _inferLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cache[query] = predictions;
            if (_cache.Count > _maxCacheSize)
            {
                var key = _cache.Keys.FirstOrDefault();
                if (key != null) _cache.TryRemove(key, out _);
            }
        }
        finally
        {
            _inferLock.Release();
        }

        sw.Stop();

        var result = new NeedleRouteResult
        {
            Tier = DetermineTier(predictions),
            Predictions = predictions,
            InferenceMs = sw.ElapsedMilliseconds,
            IsLocal = predictions.Count > 0
        };

        _logger.LogDebug("Needle route: tier={Tier}, top={TopTool}:{Conf:F2}, {Ms}ms",
            result.Tier,
            predictions.FirstOrDefault()?.ToolName ?? "unknown",
            predictions.FirstOrDefault()?.Confidence ?? 0,
            result.InferenceMs);

        return result;
    }

    public List<AITool> SelectToolsFromRoute(NeedleRouteResult route, IEnumerable<AITool> allTools, int maxTools = 8)
    {
        var toolSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<AITool>();

        foreach (var pred in route.Predictions.Where(p => p.Confidence > 0.3f))
            toolSet.Add(pred.ToolName);

        foreach (var tool in allTools)
        {
            if (toolSet.Contains(tool.Name))
                selected.Add(tool);

            foreach (var (toolName, keywords) in ExpansionRules)
            {
                if (toolSet.Contains(toolName))
                {
                    foreach (var kw in keywords)
                    {
                        if (tool.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                            (tool.Description?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false))
                        {
                            if (!selected.Contains(tool))
                                selected.Add(tool);
                            break;
                        }
                    }
                }
            }
        }

        selected.AddRange(allTools
            .Where(t => t.Name is "web_search" or "shell_exec" or "datetime_now")
            .Where(t => !selected.Contains(t)));

        return selected.Distinct().Take(maxTools).ToList();
    }

    private async Task<List<NeedleToolPrediction>> OnnxPredictAsync(string query, CancellationToken ct)
    {
        if (_session == null)
            return KeywordPredict(query);

        var tokens = Tokenize(query);
        var inputTensor = new DenseTensor<long>(
            tokens.Select(t => (long)t).ToArray(),
            new[] { 1, tokens.Length });

        var attentionMask = new DenseTensor<long>(
            tokens.Select(t => t > 0 ? 1L : 0L).ToArray(),
            new[] { 1, tokens.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        };

        var results = await Task.Run(() =>
        {
            using var output = _session.Run(inputs);
            var logitsTensor = output.First(o => o.Name is "logits" or "output").AsTensor<float>();
            return logitsTensor.ToArray();
        }, ct).ConfigureAwait(false);

        return TopK(results, 5);
    }

    private static List<NeedleToolPrediction> KeywordPredict(string query)
    {
        var queryLower = query.ToLowerInvariant();
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var kw in ChatOnlyKeywords)
        {
            if (queryLower.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return new List<NeedleToolPrediction>
                {
                    new() { ToolName = "chat", Confidence = 0.95f, Rank = 1 }
                };
            }
        }

        foreach (var (tool, keywords) in ExpansionRules)
        {
            var score = 0f;
            var exactHits = 0;
            foreach (var kw in keywords)
            {
                var kwLower = kw.ToLowerInvariant();
                if (queryLower.Contains(kwLower, StringComparison.OrdinalIgnoreCase))
                {
                    score += kw.Length * 0.1f;
                    exactHits++;

                    var beforeAfterPattern = $@"\b{Regex.Escape(kwLower)}\b";
                    if (Regex.IsMatch(queryLower, beforeAfterPattern))
                        score += 0.2f;
                }
            }

            if (exactHits > 0)
            {
                score = Math.Min(score * (1f + (exactHits - 1) * 0.3f) / 5f, 1.0f);
                scores[tool] = score;
            }
        }

        if (scores.Count == 0)
        {
            scores["chat"] = 0.8f;
            scores["knowledge_search"] = 0.4f;
        }
        else if (!scores.ContainsKey("chat"))
        {
            scores["chat"] = 0.3f;
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select((kv, i) => new NeedleToolPrediction
            {
                ToolName = kv.Key,
                Confidence = kv.Value,
                Rank = i + 1
            })
            .ToList();
    }

    private static List<NeedleToolPrediction> TopK(float[] logits, int k)
    {
        return logits
            .Select((v, i) => (Value: v, Index: i))
            .OrderByDescending(x => x.Value)
            .Take(k)
            .Select((x, rank) => new NeedleToolPrediction
            {
                ToolName = x.Index < ToolCategories.Length ? ToolCategories[x.Index] : $"tool_{x.Index}",
                Confidence = Sigmoid(x.Value),
                Rank = rank + 1
            })
            .ToList();
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static string DetermineTier(List<NeedleToolPrediction> predictions)
    {
        if (predictions.Count == 0) return "l1";

        var top = predictions[0];
        if (top.Confidence >= 0.8f) return "l0";
        if (top.Confidence >= 0.5f) return "l1";
        return "l2";
    }

    private long[] Tokenize(string text)
    {
        var tokens = new List<long> { 1L };
        var normalized = text.ToLowerInvariant();
        var words = Regex.Split(normalized, @"\s+|(?<=[。，！？,.!?])").Where(w => w.Length > 0);

        foreach (var word in words)
        {
            var hash = (uint)word.GetHashCode();
            var token = 2L + (hash % (_vocabSize - 3));
            tokens.Add(token);
        }

        if (tokens.Count > _maxLength)
            tokens = tokens.Take(_maxLength).ToList();

        tokens.Add(2L);

        while (tokens.Count < _maxLength)
            tokens.Add(0L);

        return tokens.ToArray();
    }

    public void Dispose()
    {
        _inferLock.Dispose();
        _session?.Dispose();
    }
}
