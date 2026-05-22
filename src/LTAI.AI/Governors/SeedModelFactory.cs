using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 基于规则的种子细胞引擎 ====================
// 用于冷启动阶段，提供基础领域识别能力，无需预训练模型

public sealed record RuleMapping
{
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public string Label { get; init; } = "";
    public float Confidence { get; init; } = 0.6f;
}

public sealed class RuleBasedCellEngine
{
    private readonly string _domain;
    private readonly List<RuleMapping> _rules;
    private readonly ILogger<RuleBasedCellEngine> _logger;

    public RuleBasedCellEngine(string domain, List<RuleMapping> rules, ILogger<RuleBasedCellEngine>? logger = null)
    {
        _domain = domain;
        _rules = rules;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleBasedCellEngine>.Instance;
    }

    public InferenceResult Predict(string text)
    {
        var lower = text.ToLowerInvariant();
        var bestMatch = (Label: "unknown", Confidence: 0.0f);

        foreach (var rule in _rules)
        {
            var matchCount = rule.Keywords.Count(kw => lower.Contains(kw));
            if (matchCount > 0)
            {
                var confidence = Math.Min(0.95f, rule.Confidence + (matchCount * 0.05f));
                if (confidence > bestMatch.Confidence)
                {
                    bestMatch = (rule.Label, confidence);
                }
            }
        }

        return new InferenceResult
        {
            PredictedLabel = bestMatch.Label,
            Confidence = bestMatch.Confidence,
            LatencyMs = 0.1f,
            ModelType = "rule_seed"
        };
    }

    public bool IsReady => true;
    public string Domain => _domain;
}

// ==================== 规则引擎适配器 (适配 OnnxCellEngine 接口) ====================

public sealed class RuleEngineAdapter : ICellEngine
{
    private readonly RuleBasedCellEngine _ruleEngine;

    public RuleEngineAdapter(RuleBasedCellEngine ruleEngine)
    {
        _ruleEngine = ruleEngine;
    }

    public bool IsReady => true;
    public string Domain => _ruleEngine.Domain;

    public InferenceResult Predict(string text)
    {
        return _ruleEngine.Predict(text);
    }

    public void Dispose() { }
}

// ==================== 种子模型生成器 ====================

public static class SeedModelFactory
{
    public static Dictionary<string, RuleBasedCellEngine> CreateDefaultSeeds()
    {
        var seeds = new Dictionary<string, RuleBasedCellEngine>();

        // 1. 问候领域
        seeds["greeting"] = new RuleBasedCellEngine("greeting", new List<RuleMapping>
        {
            new() { Keywords = new[] { "hello", "hi", "hey", "你好", "早上好", "晚上好" }, Label = "greeting", Confidence = 0.8f },
            new() { Keywords = new[] { "bye", "再见", "拜拜", "see you" }, Label = "farewell", Confidence = 0.8f },
            new() { Keywords = new[] { "thank", "谢谢", "感谢" }, Label = "gratitude", Confidence = 0.85f },
        });

        // 2. 代码基础领域
        seeds["code"] = new RuleBasedCellEngine("code", new List<RuleMapping>
        {
            new() { Keywords = new[] { "function", "函数", "method", "方法" }, Label = "function_concept", Confidence = 0.7f },
            new() { Keywords = new[] { "class", "类", "object", "对象" }, Label = "oop_concept", Confidence = 0.7f },
            new() { Keywords = new[] { "bug", "error", "错误", "debug", "调试" }, Label = "debugging", Confidence = 0.75f },
            new() { Keywords = new[] { "api", "接口", "rest", "http" }, Label = "api_concept", Confidence = 0.7f },
        });

        // 3. 数学基础领域
        seeds["math"] = new RuleBasedCellEngine("math", new List<RuleMapping>
        {
            new() { Keywords = new[] { "calculate", "计算", "sum", "求和" }, Label = "arithmetic", Confidence = 0.75f },
            new() { Keywords = new[] { "equation", "方程", "solve", "求解" }, Label = "algebra", Confidence = 0.7f },
            new() { Keywords = new[] { "triangle", "三角形", "pythagorean", "勾股" }, Label = "geometry", Confidence = 0.75f },
        });

        // 4. 系统/配置领域
        seeds["system"] = new RuleBasedCellEngine("system", new List<RuleMapping>
        {
            new() { Keywords = new[] { "install", "安装", "setup", "设置" }, Label = "installation", Confidence = 0.7f },
            new() { Keywords = new[] { "config", "配置", "settings", "选项" }, Label = "configuration", Confidence = 0.7f },
            new() { Keywords = new[] { "log", "日志", "error", "报错" }, Label = "troubleshooting", Confidence = 0.75f },
        });

        return seeds;
    }
}
