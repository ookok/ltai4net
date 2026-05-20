namespace LTAI.Core.System;

public interface ITextClassifier
{
    string Classify(string input);
}

public sealed class KeywordClassifier : ITextClassifier
{
    private readonly (string Category, string[] Keywords)[] _rules;
    private readonly string _defaultCategory;

    public KeywordClassifier(
        (string Category, string[] Keywords)[] rules,
        string defaultCategory = "general")
    {
        _rules = rules;
        _defaultCategory = defaultCategory;
    }

    public string Classify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return _defaultCategory;

        var lower = input.ToLowerInvariant();
        foreach (var (category, keywords) in _rules)
        {
            foreach (var kw in keywords)
            {
                if (lower.Contains(kw.ToLowerInvariant()))
                    return category;
            }
        }
        return _defaultCategory;
    }
}

public sealed class MultiKeywordClassifier : ITextClassifier
{
    private readonly (string Category, string[] Keywords)[] _rules;
    private readonly int _minMatches;

    public MultiKeywordClassifier(
        (string Category, string[] Keywords)[] rules,
        int minMatches = 1)
    {
        _rules = rules;
        _minMatches = minMatches;
    }

    public string Classify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "general";

        var lower = input.ToLowerInvariant();
        var scores = new Dictionary<string, int>();

        foreach (var (category, keywords) in _rules)
        {
            int count = keywords.Count(kw => lower.Contains(kw.ToLowerInvariant()));
            if (count >= _minMatches)
                scores[category] = count;
        }

        if (scores.Count == 0)
            return "general";

        return scores.MaxBy(kv => kv.Value).Key;
    }
}

public static class ClassificationRegistry
{
    public static readonly ITextClassifier EndpointCategory = new KeywordClassifier([
        ("llm", ["openai", "llm", "language model"]),
        ("database", ["database", "postgres", "mysql", "redis"]),
        ("mcp", ["mcp", "tool"]),
        ("utility", ["weather", "news"]),
        ("knowledge", ["graph", "knowledge"]),
        ("storage", ["storage", "file"])
    ], "api");

    public static readonly ITextClassifier AuthType = new KeywordClassifier([
        ("bearer", ["bearer"]),
        ("token", ["token"]),
        ("api_key", ["api key", "apikey", "auth"])
    ]);

    public static readonly ITextClassifier ModelCapability = new MultiKeywordClassifier([
        ("completion", ["gpt", "claude", "deepseek", "qwen", "llama", "gemini"]),
        ("code", ["code", "coder", "copilot"]),
        ("embedding", ["embed", "bge", "e5"]),
        ("vision", ["vision", "vl", "multimodal"]),
        ("audio", ["audio", "whisper", "tts"]),
        ("image_generation", ["image", "dalle", "stable"]),
        ("reasoning", ["reason", "o1", "o3"])
    ]);

    public static readonly ITextClassifier UrlCapability = new MultiKeywordClassifier([
        ("completion", ["openai", "deepseek", "qwen", "anthropic", "claude", "gemini"]),
        ("code", ["deepseek", "anthropic", "claude"]),
        ("embedding", ["embed", "vector"]),
        ("vision", ["qwen", "gemini", "google"]),
        ("reasoning", ["deepseek"]),
        ("graph", ["graph", "neo4j"]),
        ("weather", ["weather"]),
        ("search", ["search", "tavily"]),
        ("database", ["db", "sql", "mongo", "redis"]),
        ("chat", ["openai", "deepseek", "qwen", "anthropic", "claude", "gemini", "google", "ollama", "local"])
    ]);

    public static readonly ITextClassifier Intent = new MultiKeywordClassifier([
        ("code", ["code", "function", "class", "bug", "error", "fix", "implement", "refactor", "test", "compile", "syntax", "import", "package"]),
        ("reasoning", ["why", "explain", "reason", "analyze", "compare", "contrast", "evaluate", "assess", "prove", "logic", "cause"]),
        ("chat", ["hello", "hi", "how are you", "thanks", "help", "what is", "tell me", "who", "when"]),
        ("search", ["find", "search", "lookup", "google", "where is", "locate"]),
        ("long_context", ["summarize", "summary", "document", "article", "report", "write", "draft", "essay"])
    ]);

    public static readonly ITextClassifier ProviderMode = new KeywordClassifier([
        ("Self", ["我的", "my", "个人", "personal", "偏好", "preference"]),
        ("ThirdParty", ["代表我", "on behalf", "替我", "present me"]),
        ("Enhance", ["帮我", "help me", "补充上下文", "add context", "丰富", "enrich", "补充"]),
        ("Critic", ["检查", "review", "评估", "critique", "建议", "suggestion", "改进", "improve"])
    ], "Self");

    public static readonly ITextClassifier ReasoningType = new KeywordClassifier([
        ("Math", ["+", "-", "*", "/", "=", "calculate", "solve", "计算", "等于", "方程"]),
        ("Logic", ["if", "then", "therefore", "implies", "premise", "syllogism", "如果", "那么"]),
        ("Dialectical", ["should", "better", "versus", "compare", "pros and cons", "advantage", "disadvantage", "应该", "优劣", "对比"]),
        ("Attribution", ["why", "cause", "because", "root cause", "led to", "为什么", "原因", "导致"])
    ], "Logic");

    public static readonly ITextClassifier CodeLanguage = new KeywordClassifier([
        ("CSharp", ["using System", "namespace"]),
        ("TypeScript", ["import React", "export default"]),
        ("Python", ["def", "import"]),
        ("Go", ["func", "package"]),
        ("Rust", ["fn", "let mut"]),
        ("Java", ["public class", "void"]),
        ("Sql", ["SELECT", "CREATE TABLE"]),
        ("Html", ["<!DOCTYPE html", "<div"])
    ], "Unknown");

    public static readonly ITextClassifier SuspiciousContent = new KeywordClassifier([
        ("dangerous", ["delete", "drop", "exec", "sudo", "rm ", "shutdown", "system", "os.", "subprocess", "__import__", "eval", "ignore", "bypass", "jailbreak"])
    ]);

    public static readonly ITextClassifier MemoryTag = new MultiKeywordClassifier([
        ("error", ["error", "bug", "fail", "crash"]),
        ("fix", ["fix", "solution", "resolve"]),
        ("pattern", ["pattern", "template"]),
        ("security", ["security", "vuln", "attack"]),
        ("config", ["config", "setup", "install"]),
        ("api", ["api", "endpoint"])
    ], 0);

    public static readonly ITextClassifier TaskPattern = new KeywordClassifier([
        ("comparison", ["compare", "对比", "diff"]),
        ("analysis", ["analyze", "分析", "研究"]),
        ("decomposition", ["decompose", "分解", "拆分"]),
        ("evaluation", ["evaluate", "评估", "判断"]),
        ("generation", ["generate", "生成", "创建"]),
        ("verification", ["verify", "验证", "检查"])
    ], "general");

    public static readonly ITextClassifier StepType = new KeywordClassifier([
        ("conclusion", ["therefore", "conclude", "thus"]),
        ("premise", ["because", "reason", "since"]),
        ("assumption", ["assume", "hypothesis", "suppose"]),
        ("procedure", ["step", "first", "next"]),
        ("example", ["example", "for instance"])
    ], "general");

    public static readonly ITextClassifier ToolNeed = new MultiKeywordClassifier([
        ("code_tool", ["代码", "code"]),
        ("file_access", ["文件", "file"]),
        ("web_search", ["搜索", "search"])
    ], 0);

    public static readonly ITextClassifier EmotionGroup = new KeywordClassifier([
        ("anger", ["anger", "rage", "irritation"]),
        ("fear", ["fear", "terror", "anxiety"]),
        ("sadness", ["sadness", "grief", "disappointment"]),
        ("joy", ["joy", "excitement", "ecstasy"]),
        ("surprise", ["surprise", "curiosity"]),
        ("disgust", ["disgust", "contempt"]),
        ("trust", ["trust", "acceptance", "gratitude"])
    ], "neutral");

    public static readonly ITextClassifier RoutingTaskType = new KeywordClassifier([
        ("code", ["code", "function", "class", "bug"]),
        ("reasoning", ["reason", "analyze", "logic"]),
        ("chat", ["chat", "conversation", "talk"]),
        ("search", ["search", "find", "lookup"]),
        ("long_context", ["document", "context", "summarize"])
    ], "chat");

    public static readonly ITextClassifier ContentFormat = new KeywordClassifier([
        ("eia", ["环评", "环境影响"]),
        ("code", ["class", "public", "def"]),
        ("table", ["表格", "|--"]),
        ("markdown", ["#", "##"])
    ], "text");

    public static readonly ITextClassifier SourceCredibility = new KeywordClassifier([
        ("high", ["gov", "edu", "标准"]),
        ("medium", ["wiki", "baidu.com"])
    ], "standard");

    public static readonly ITextClassifier DomainSignal = new KeywordClassifier([
        ("eia", ["环评", "EIA", "环境"]),
        ("security", ["安全"]),
        ("translate", ["翻译"]),
        ("document", ["文档"])
    ]);

    public static readonly ITextClassifier PipelineTrigger = new KeywordClassifier([
        ("report_pipeline", ["report", "报告"]),
        ("search_pipeline", ["search", "搜索"])
    ], "default_pipeline");

    public static readonly (string Category, string[] Keywords)[] ContentTopics = [
        ("code", ["code", "代码"]),
        ("file", ["file", "文件"]),
        ("error", ["error", "错误"]),
        ("fix", ["fix", "修复"]),
        ("api", ["api", "接口"]),
        ("database", ["database", "数据库"]),
        ("test", ["test", "测试"]),
        ("deploy", ["deploy", "部署"]),
        ("config", ["config", "配置"]),
        ("search", ["search", "搜索"]),
        ("query", ["query", "查询"]),
        ("review", ["review", "审查"])
    ];

    public static readonly ITextClassifier RelationshipType = new KeywordClassifier([
        ("is", ["is", "are", "was", "were"]),
        ("has", ["has", "have", "contains"]),
        ("causes", ["causes", "leads to", "results in"]),
        ("indicates", ["indicates", "suggests", "implies"]),
        ("belongs_to", ["belongs to", "is a type of", "is part of"])
    ], "is");

    public static readonly (string Category, string[] Keywords)[] SovereignEvidence = [
        ("evidence", ["because", "since", "due to", "based on", "evidence", "data shows",
            "因为", "由于", "根据", "数据", "证据", "依据",
            "log", "trace", "record", "source", "reference"])
    ];

    public static readonly (string Category, string[] Keywords)[] SovereignConflict = [
        ("conflict", ["however", "but", "although", "contrary", "conflict", "disagree",
            "但是", "然而", "不过", "矛盾", "冲突", "不一致",
            "wrong", "incorrect", "false", "error", "mistake",
            "错误", "不正确", "误解"])
    ];

    public static readonly (string Category, string[] Keywords)[] SovereignJudgment = [
        ("judgment", ["I think", "I believe", "my analysis", "I conclude", "in my opinion",
            "我认为", "我的分析", "结论是", "根据我的判断",
            "verify", "validate", "confirm", "check", "examine",
            "验证", "确认", "检查", "核实"])
    ];

    public static readonly (string Category, string[] Keywords)[] SovereignSycophancy = [
        ("sycophancy", ["agree with", "as you said", "you are right", "following",
            "同意", "如你所说", "你说的对", "按照你的", "遵循"])
    ];

    public static readonly (string Category, string[] Keywords)[] SovereignConfidence = [
        ("confidence", ["clearly", "definitely", "certainly", "undoubtedly", "obviously",
            "显然", "明确", "肯定", "确实", "确定"])
    ];

    public static readonly (string Category, string[] Keywords)[] SovereignAnswer = [
        ("answer", ["answer is", "result is", "solution is", "correct id", "true id",
            "答案是", "结果是", "正确ID", "真实ID"])
    ];
}
