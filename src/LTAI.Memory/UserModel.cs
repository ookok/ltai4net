using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Memory.Models;

namespace LTAI.Memory;

public static class UserModelConstants
{
    public const float MOMENTUM_BETA = 0.85f;
    public const int MAX_CORRECTIONS = 20;
    public const int CORRECTION_CONFIDENCE_THRESHOLD = 2;

    public static readonly Dictionary<string, float> DEFAULT_TRAITS = new()
    {
        ["EngagementDepth"] = 0.5f,
        ["TechnicalSophistication"] = 0.5f,
        ["PatienceTolerance"] = 0.7f,
        ["FeedbackDirectness"] = 0.5f,
        ["TopicBreadth"] = 0.4f,
        ["InteractionRegularity"] = 0.5f,
        ["DelegationComfort"] = 0.5f
    };

    public static readonly Dictionary<string, (List<string> Inc, List<string> Dec)> TRAIT_SIGNALS = new()
    {
        ["EngagementDepth"] = (
            ["detailed", "elaborate", "deep", "thorough", "explain more", "in detail", "详细", "深入", "仔细", "全面", "多说一点"],
            ["brief", "short", "quick", "simple", "just tell me", "summarize", "简要", "简短", "快速", "简单", "一句话"]
        ),
        ["TechnicalSophistication"] = (
            ["api", "python", "code", "function", "algorithm", "database", "sql", "json", "xml", "docker", "git", "compile", "debug", "framework", "library", "class", "interface", "编程", "算法", "数据库", "接口", "框架"],
            ["easy", "beginner", "non-technical", "what is", "how do i start", "新手", "入门", "基础", "简单操作"]
        ),
        ["PatienceTolerance"] = (
            ["take your time", "no rush", "it's ok", "whenever", "不急", "慢慢来", "没关系", "有空再说"],
            ["hurry", "quick", "fast", "now", "immediately", "asap", "urgent", "快", "马上", "立刻", "赶紧", "急"]
        ),
        ["FeedbackDirectness"] = (
            ["wrong", "incorrect", "no", "bad", "not what i want", "change", "fix", "错了", "不对", "不好", "改", "修"],
            ["maybe", "perhaps", "i think", "it seems", "could be", "possibly", "可能", "也许", "似乎", "大概"]
        ),
        ["TopicBreadth"] = (
            ["also", "another", "besides", "additionally", "furthermore", "different topic", "by the way", "还有", "另外", "顺便", "再说", "切换话题"],
            ["same", "similar", "like before", "as discussed", "继续", "同样的", "刚才说的"]
        ),
        ["InteractionRegularity"] = (
            ["good morning", "hello again", "back", "continuing", "daily", "我又来了", "又回来了", "继续", "每天"],
            ["new", "first time", "hi", "首次", "第一次", "新人"]
        ),
        ["DelegationComfort"] = (
            ["you decide", "you handle", "do it", "take care of", "figure out", "handle it", "你决定", "你处理", "你来弄", "交给你"],
            ["let me", "i will", "we should", "together", "我来", "我自己", "我们一起"]
        )
    };

    public static readonly Dictionary<string, List<string>> HABIT_SIGNALS = new()
    {
        ["DeepDive"] = ["深入", "详细", "deep", "detail", "explain", "展开", "elaborate", "thorough", "全面", "仔细"],
        ["QuickScan"] = ["简短的", "快速", "quick", "brief", "short", "summary", "摘要", "一句话", "简答", "简要"],
        ["VisualPreference"] = ["图", "chart", "可视化", "visual", "diagram", "图表", "流程图", "graph", "画出来", "示意图"],
        ["CodeFirst"] = ["代码", "code", "示例", "example", "snippet", "demo", "实现", "implement", "函数", "function"],
        ["StepByStep"] = ["一步一步", "step by step", "步骤", "流程", "walkthrough", "教程", "tutorial", "引导", "guide"],
        ["WhatsNew"] = ["最新", "latest", "更新", "update", "changelog", "新功能", "new feature", "变化", "change"],
        ["GetItDone"] = ["搞定", "处理", "handle", "fix", "修复", "完成", "finish", "done", "解决", "resolve"]
    };

    public static readonly Dictionary<string, List<string>> DOMAIN_GROUP_KEYWORDS = new()
    {
        ["EIA"] = ["eia", "哲学", "伦理", "意识", "自我", "consciousness", "ethics", "identity"],
        ["Code"] = ["code", "代码", "编程", "programming", "api", "function", "debug", "compile", "算法"],
        ["Document"] = ["文档", "document", "写作", "write", "article", "blog", "手册", "manual", "report", "报告"],
        ["Analysis"] = ["分析", "analysis", "数据", "data", "统计", "statistics", "insight", "图表", "chart"]
    };
}

public sealed class UserModel
{
    private static readonly Lazy<UserModel> _instance = new(() => new UserModel());
    public static UserModel GetUserModel() => _instance.Value;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".livingtree");
    private static readonly string UserModelFile = Path.Combine(DataDir, "user_model.json");

    private readonly object _lock = new();

    public UserProfile Profile { get; private set; }
    private bool _synced;
#pragma warning disable CS0169
    private object? _persona;
#pragma warning restore CS0169

    private Lazy<PersonaMemory>? _personaLazy;
    public PersonaMemory? Persona
    {
        get
        {
            _personaLazy ??= new Lazy<PersonaMemory>(() => PersonaMemory.GetPersonaMemory());
            return _personaLazy.Value;
        }
    }

    private UserModel()
    {
        Profile = Load();
    }

    public void RecordCorrection(string statement, string category = "general")
    {
        lock (_lock)
        {
            var trimmed = statement.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            var existing = Profile.Corrections.Find(c =>
                c.Correction.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                Profile.Corrections.Remove(existing);
                Profile.Corrections.Add(existing with
                {
                    Count = existing.Count + 1,
                    LastSeen = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
                });
            }
            else
            {
                Profile.Corrections.Add(new UserCorrection(
                    Trigger: "",
                    Correction: trimmed,
                    Category: category,
                    Count: 1,
                    LastSeen: (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds,
                    Source: "user"
                ));
            }

            if (Profile.Corrections.Count > UserModelConstants.MAX_CORRECTIONS)
            {
                Profile = Profile with
                {
                    Corrections = Profile.Corrections
                        .OrderByDescending(c => c.Count)
                        .Take(UserModelConstants.MAX_CORRECTIONS)
                        .ToList()
                };
            }

            MarkDirty();
        }
    }

    public void SetPreference(string key, string value)
    {
        RecordCorrection($"{key}: {value}", "preference");
    }

    public List<string> GetInstructionRules()
    {
        lock (_lock)
        {
            return Profile.Corrections
                .Where(c => c.Count >= UserModelConstants.CORRECTION_CONFIDENCE_THRESHOLD)
                .Select(c => c.ToRule())
                .ToList();
        }
    }

    public void ObserveMessage(string message, bool autoPro = false)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        lock (_lock)
        {
            ParseCorrection(message);
            _inferTraits(message);
            _updateHabitSignals(message);
            DetectDomain(message);

            MarkDirty();
        }
    }

    public Dictionary<string, float> GetUserTraits()
    {
        lock (_lock)
        {
            return Profile.Traits.ToDict();
        }
    }

    public Dictionary<string, float> GetHabitSignals()
    {
        lock (_lock)
        {
            return new Dictionary<string, float>(Profile.HabitSignals);
        }
    }

    public Dictionary<string, object> GetAdaptiveCommunicationStyle()
    {
        lock (_lock)
        {
            var traits = Profile.Traits.ToDict();
            var habits = new Dictionary<string, float>(Profile.HabitSignals);

            var engagement = traits.GetValueOrDefault("EngagementDepth", 0.5f);
            var patience = traits.GetValueOrDefault("PatienceTolerance", 0.7f);
            var technical = traits.GetValueOrDefault("TechnicalSophistication", 0.5f);
            var delegation = traits.GetValueOrDefault("DelegationComfort", 0.5f);

            var deepDive = habits.GetValueOrDefault("DeepDive", 0.3f);
            var quickScan = habits.GetValueOrDefault("QuickScan", 0.3f);
            var stepByStep = habits.GetValueOrDefault("StepByStep", 0.3f);

            var temperature = Math.Clamp(0.3f + engagement * 0.5f, 0.1f, 1.0f);
            var verbosity = deepDive > quickScan ? "detailed" : "concise";
            var formality = technical > 0.7f ? "technical" : patience > 0.6f ? "polite" : "casual";

            return new Dictionary<string, object>
            {
                ["temperature"] = temperature,
                ["verbosity"] = verbosity,
                ["formality"] = formality,
                ["step_by_step"] = stepByStep > 0.4f,
                ["autonomous"] = delegation > 0.6f
            };
        }
    }

    public UserBeliefState InferBeliefState(string message, string taskContext = "")
    {
        var text = message.ToLowerInvariant();

        var topicCategories = new Dictionary<string, List<string>>
        {
            ["programming"] = ["code", "api", "function", "bug", "compile", "debug", "algorithm", "编程", "代码"],
            ["data"] = ["data", "analysis", "statistics", "chart", "dataset", "数据", "分析"],
            ["writing"] = ["write", "article", "blog", "document", "essay", "写作", "文章"],
            ["design"] = ["design", "ui", "ux", "layout", "style", "设计", "界面"],
            ["systems"] = ["server", "docker", "deploy", "infrastructure", "network", "系统", "部署"],
            ["planning"] = ["plan", "schedule", "organize", "task", "todo", "计划", "安排"],
            ["learning"] = ["learn", "tutorial", "guide", "example", "how to", "学习", "教程"],
            ["conversation"] = ["talk", "discuss", "chat", "ask", "tell me", "聊天", "讨论"]
        };

        var knownTopics = new List<string>();
        var unknownTopics = new List<string>();

        foreach (var (topic, keywords) in topicCategories)
        {
            var hits = keywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hits >= 2)
                knownTopics.Add(topic);
            else if (hits == 1)
                unknownTopics.Add(topic);
        }

        var gapPatterns = new List<string>
        {
            "what is", "how do", "i don't know", "can you explain", "我不懂", "我不知道",
            "为什么", "怎么", "什么是", "how does", "what does", "explain"
        };
        var gapHits = gapPatterns.Count(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

        var statedGoals = _extractGoals(message);
        var impliedWants = _extractImplicitWants(message);

        var frustration = Math.Clamp(gapHits * 0.15f - (knownTopics.Count > 0 ? 0.05f : 0f), 0f, 1f);
        var satisfaction = Math.Clamp(knownTopics.Count * 0.08f - (unknownTopics.Count > 0 ? 0.02f : 0f), 0f, 1f);

        var attentionSpan = message.Length switch
        {
            < 50 => "short",
            < 200 => "medium",
            _ => "long"
        };

        return new UserBeliefState(
            KnownTopics: knownTopics,
            UnknownTopics: unknownTopics,
            StatedGoals: statedGoals,
            ImpliedWants: impliedWants,
            FrustrationLevel: frustration,
            SatisfactionLevel: satisfaction,
            AttentionSpan: attentionSpan
        );
    }

    public List<KnowledgeGap> DetectKnowledgeGaps(string message, string taskDomain = "general")
    {
        var gaps = new List<KnowledgeGap>();
        var text = message.ToLowerInvariant();

        var gapPatterns = new (string Pattern, string Topic)[]
        {
            ("what is", "definition"),
            ("how do i", "procedure"),
            ("why does", "causality"),
            ("when should", "timing"),
            ("where is", "location"),
            ("who can", "actor"),
            ("can you explain", "clarification"),
            ("i don't understand", "comprehension"),
            ("unclear about", "ambiguity"),
            ("confused about", "confusion")
        };

        foreach (var (pattern, topic) in gapPatterns)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                var evidence = message[Math.Max(0, idx)..Math.Min(message.Length, idx + pattern.Length + 30)];
                gaps.Add(new KnowledgeGap(
                    Topic: $"{taskDomain}/{topic}",
                    Evidence: evidence,
                    Severity: 0.6f,
                    Timestamp: (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
                ));
            }
        }

        return gaps;
    }

    public ExpectationModel InferExpectation(string message, string lastResponse = "", int conversationTurn = 0)
    {
        var text = message.ToLowerInvariant();

        var nextAction = "respond";
        if (text.Contains("write") || text.Contains("generate") || text.Contains("create") || text.Contains("写") || text.Contains("生成"))
            nextAction = "generate";
        else if (text.Contains("analyze") || text.Contains("analysis") || text.Contains("分析"))
            nextAction = "analyze";
        else if (text.Contains("fix") || text.Contains("debug") || text.Contains("solve") || text.Contains("修复") || text.Contains("解决"))
            nextAction = "fix";

        var responseType = "conversational";
        if (text.Contains("code") || text.Contains("代码") || text.Contains("programming"))
            responseType = "technical";
        else if (nextAction == "fix")
            responseType = "instructional";

        var detailLevel = "standard";
        if (text.Contains("detailed") || text.Contains("详细") || text.Contains("elaborate"))
            detailLevel = "detailed";
        else if (text.Contains("brief") || text.Contains("quick") || text.Contains("简短"))
            detailLevel = "concise";

        var deadlinePressure = 0f;
        if (text is "urgent" or "asap" or "急" or "马上" or "立刻")
            deadlinePressure = 0.8f;
        else if (text.Contains("when you can") || text.Contains("慢慢"))
            deadlinePressure = 0.1f;
        else
            deadlinePressure = 0.3f;

        var implicitQuestion = "";
        if (text.Contains("not sure") || text.Contains("不确定"))
            implicitQuestion = "confirmation";
        else if (text.Contains("or") && text.Contains("?"))
            implicitQuestion = "comparison";

        return new ExpectationModel(
            NextActionExpected: nextAction,
            ExpectedResponseType: responseType,
            ExpectedDetailLevel: detailLevel,
            DeadlinePressure: deadlinePressure,
            ImplicitQuestion: implicitQuestion
        );
    }

    public EmpathySignal InferEmpathy(string message, string taskContext = "")
    {
        var text = message.ToLowerInvariant();

        var emotionKeywords = new Dictionary<string, List<string>>
        {
            ["frustration"] = ["frustrated", "annoying", "not working", "broken", "doesn't work", "烦", "搞不定", "不行"],
            ["curiosity"] = ["curious", "interesting", "wonder", "how does", "what if", "好奇", "有意思"],
            ["satisfaction"] = ["great", "nice", "perfect", "thanks", "good", "很好", "不错", "完美", "谢谢"],
            ["confusion"] = ["confused", "unclear", "don't get", "what does", "不明白", "搞不懂", "什么意思"],
            ["urgency"] = ["urgent", "asap", "quickly", "hurry", "now", "急", "马上", "赶快"],
            ["appreciation"] = ["thank you", "appreciate", "helpful", "thanks", "感谢", "多谢", "帮大忙"],
            ["disappointment"] = ["disappointed", "not good", "could be better", "meh", "失望", "不太好"]
        };

        string primaryEmotion = "neutral";
        string secondaryEmotion = "";
        float maxConfidence = 0f;
        float secondConfidence = 0f;

        foreach (var (emotion, keywords) in emotionKeywords)
        {
            var hits = keywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            var confidence = MathF.Min(1f, hits * 0.25f);
            if (confidence > maxConfidence)
            {
                secondConfidence = maxConfidence;
                secondaryEmotion = primaryEmotion;
                maxConfidence = confidence;
                primaryEmotion = emotion;
            }
            else if (confidence > secondConfidence)
            {
                secondConfidence = confidence;
                secondaryEmotion = emotion;
            }
        }

        var cogLoadKeywords = new[] { "complex", "complicated", "advanced", "hard", "difficult", "复杂", "难", "高级" };
        var cognitiveLoad = cogLoadKeywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase)) * 0.2f;
        cognitiveLoad = Math.Min(1f, cognitiveLoad + 0.2f);

        var urgencyKeywords = new[] { "urgent", "asap", "hurry", "now", "quick", "急", "马上", "快" };
        var timePressure = urgencyKeywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase)) * 0.25f;
        timePressure = Math.Min(1f, timePressure);

        string socialTone = primaryEmotion switch
        {
            "frustration" => "negative",
            "satisfaction" or "appreciation" => "positive",
            "curiosity" => "exploratory",
            "confusion" => "inquisitive",
            "urgency" => "demanding",
            _ => "neutral"
        };

        string inferredNeed = primaryEmotion switch
        {
            "frustration" => "reassurance_and_solution",
            "curiosity" => "exploration_and_detail",
            "confusion" => "clarity_and_explanation",
            "urgency" => "speed_and_efficiency",
            "appreciation" => "reinforcement",
            "disappointment" => "improvement",
            _ => "general_assistance"
        };

        return new EmpathySignal(
            PrimaryEmotion: primaryEmotion,
            SecondaryEmotion: secondaryEmotion,
            ConfidenceLevel: maxConfidence,
            CognitiveLoad: cognitiveLoad,
            TimePressure: timePressure,
            SocialTone: socialTone,
            InferredNeed: inferredNeed
        );
    }

    public string GetEmpathyContext()
    {
        lock (_lock)
        {
            var belief = Profile.BeliefState;
            var empathy = Profile.EmpathySignal;

            var parts = new List<string>
            {
                $"用户情感: {empathy.PrimaryEmotion} (置信度 {empathy.ConfidenceLevel:F1})",
                $"认知负荷: {empathy.CognitiveLoad:F2}",
                $"挫败感: {belief.FrustrationLevel:F2}",
                $"满意度: {belief.SatisfactionLevel:F2}",
                $"已知领域: {string.Join(", ", belief.KnownTopics)}",
                $"未知领域: {string.Join(", ", belief.UnknownTopics)}"
            };

            return string.Join(" | ", parts);
        }
    }

    public void SetProjectContext(string path, string description = "")
    {
        lock (_lock)
        {
            Profile = Profile with
            {
                ProjectContext = path,
                LastUpdated = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
            };
            MarkDirty();
        }
    }

    public void SetModelPreference(string model)
    {
        lock (_lock)
        {
            Profile = Profile with
            {
                PreferredModel = model,
                LastUpdated = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
            };
            MarkDirty();
        }
    }

    public List<string> GetEnvRules()
    {
        var rules = new List<string>();
        var projectContext = Profile.ProjectContext;

        if (!string.IsNullOrEmpty(projectContext))
        {
            if (projectContext.Contains(".csproj") || projectContext.Contains(".sln"))
            {
                rules.Add("C#项目环境: 使用.NET CLI命令");
            }
            if (projectContext.Contains("package.json"))
            {
                rules.Add("Node.js项目环境: 使用npm/yarn命令");
            }
            if (projectContext.Contains("requirements.txt") || projectContext.Contains("pyproject.toml"))
            {
                rules.Add("Python项目环境: 使用pip/python命令");
            }
        }

        if (!string.IsNullOrEmpty(Profile.PreferredModel))
            rules.Add($"首选模型: {Profile.PreferredModel}");

        return rules;
    }

    public string InjectIntoPrompt(string role = "")
    {
        lock (_lock)
        {
            var parts = new List<string>();

            var corrections = Profile.Corrections
                .Where(c => c.Count >= UserModelConstants.CORRECTION_CONFIDENCE_THRESHOLD)
                .Select(c => c.ToRule())
                .ToList();
            if (corrections.Count > 0)
            {
                parts.Add("[L1 用户规则]");
                parts.AddRange(corrections.Take(5));
            }

            var traits = Profile.Traits.ToDict();
            var highTraits = traits.Where(kv => kv.Value > 0.65f).Select(kv => $"{kv.Key}={kv.Value:F2}").ToList();
            var lowTraits = traits.Where(kv => kv.Value < 0.35f).Select(kv => $"{kv.Key}={kv.Value:F2}").ToList();

            var habitParts = new List<string>();
            foreach (var (name, val) in Profile.HabitSignals)
            {
                if (val > 0.4f)
                    habitParts.Add($"{name}={val:F2}");
            }

            if (highTraits.Count > 0 || lowTraits.Count > 0 || habitParts.Count > 0)
            {
                parts.Add("[L2 用户画像]");
                if (highTraits.Count > 0)
                    parts.Add($"  高: {string.Join(", ", highTraits)}");
                if (lowTraits.Count > 0)
                    parts.Add($"  低: {string.Join(", ", lowTraits)}");
                if (habitParts.Count > 0)
                    parts.Add($"  习惯: {string.Join(", ", habitParts)}");
            }

            if (!string.IsNullOrEmpty(Profile.ProjectContext))
            {
                parts.Add("[L3 项目上下文]");
                parts.Add($"  项目: {Profile.ProjectContext}");
                var envRules = GetEnvRules();
                if (envRules.Count > 0)
                    parts.Add($"  环境: {string.Join("; ", envRules)}");
            }

            if (Persona is not null)
            {
                try
                {
                    var personaCtx = "";
                    var profile = Persona.GetProfile("default");
                    if (profile.TotalFacts > 0)
                    {
                        personaCtx = Persona.GetDomainSummary("default");
                    }
                    if (!string.IsNullOrEmpty(personaCtx))
                    {
                        parts.Add("[Persona]");
                        parts.Add(personaCtx);
                    }
                }
                catch { /* non-fatal */ }
            }

            if (!string.IsNullOrEmpty(role) && role != "system")
                parts.Insert(0, $"[Role: {role}]");

            return string.Join("\n", parts);
        }
    }

    public string InjectMinimal()
    {
        lock (_lock)
        {
            var traits = Profile.Traits.ToDict();
            var dominant = traits.Where(kv => kv.Value > 0.6f).Select(kv => kv.Key[..2]).ToList();
            return $"usr:{string.Join(",", dominant)}|corr:{Profile.Corrections.Count}";
        }
    }

    private void ParseCorrection(string message)
    {
        var patterns = new Dictionary<string, System.Text.RegularExpressions.Regex>
        {
            ["replace"] = new(@"不要\s*(.+?)\s*要用\s*(.+)", System.Text.RegularExpressions.RegexOptions.Compiled),
            ["dont"] = new(@"别\s*(.+)", System.Text.RegularExpressions.RegexOptions.Compiled),
            ["future"] = new(@"以后\s*(.+)", System.Text.RegularExpressions.RegexOptions.Compiled),
            ["prefer"] = new(@"我喜欢\s*(.+)", System.Text.RegularExpressions.RegexOptions.Compiled)
        };

        foreach (var (patternType, regex) in patterns)
        {
            var matches = regex.Matches(message);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                switch (patternType)
                {
                    case "replace":
                        if (match.Groups.Count >= 3)
                            RecordCorrection($"不要{match.Groups[1].Value.Trim()}，要用{match.Groups[2].Value.Trim()}", "correction");
                        break;
                    case "dont":
                        RecordCorrection($"不要{match.Groups[1].Value.Trim()}", "avoidance");
                        break;
                    case "future":
                        RecordCorrection($"以后{match.Groups[1].Value.Trim()}", "future_rule");
                        break;
                    case "prefer":
                        RecordCorrection(match.Groups[1].Value.Trim(), "preference");
                        break;
                }
            }
        }
    }

    private void DetectDomain(string message)
    {
        var text = message.ToLowerInvariant();
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        foreach (var (domain, keywords) in UserModelConstants.DOMAIN_GROUP_KEYWORDS)
        {
            var hits = keywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hits > 0)
            {
                if (!Profile.DomainAffinity.TryGetValue(domain, out var current))
                    current = 0;
                Profile.DomainAffinity[domain] = current + hits;
            }
        }

        MarkDirty();
    }

    private void _inferTraits(string message)
    {
        var text = message.ToLowerInvariant();
        var inferred = new Dictionary<string, float>();

        foreach (var (trait, (inc, dec)) in UserModelConstants.TRAIT_SIGNALS)
        {
            var incHits = inc.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            var decHits = dec.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            var total = Math.Max(inc.Count + dec.Count, 1);
            var delta = (float)(incHits - decHits) / total;

            var current = Profile.Traits.ToDict().GetValueOrDefault(trait, 0.5f);
            var rawInferred = Math.Clamp(current + delta * 0.08f, 0.05f, 0.95f);
            var newVal = UserModelConstants.MOMENTUM_BETA * current + (1f - UserModelConstants.MOMENTUM_BETA) * rawInferred;
            inferred[trait] = Math.Clamp(newVal, 0.05f, 0.95f);
        }

        Profile = Profile with
        {
            Traits = new UserTraitVector(
                EngagementDepth: inferred.GetValueOrDefault("EngagementDepth", 0.5f),
                TechnicalSophistication: inferred.GetValueOrDefault("TechnicalSophistication", 0.5f),
                PatienceTolerance: inferred.GetValueOrDefault("PatienceTolerance", 0.7f),
                FeedbackDirectness: inferred.GetValueOrDefault("FeedbackDirectness", 0.5f),
                TopicBreadth: inferred.GetValueOrDefault("TopicBreadth", 0.4f),
                InteractionRegularity: inferred.GetValueOrDefault("InteractionRegularity", 0.5f),
                DelegationComfort: inferred.GetValueOrDefault("DelegationComfort", 0.5f)
            ),
            LastUpdated = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
        };
    }

    private void _updateHabitSignals(string message)
    {
        var text = message.ToLowerInvariant();

        foreach (var (habit, keywords) in UserModelConstants.HABIT_SIGNALS)
        {
            var hits = keywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hits > 0)
            {
                var current = Profile.HabitSignals.GetValueOrDefault(habit, 0.3f);
                Profile.HabitSignals[habit] = Math.Clamp(current + hits * 0.05f, 0.05f, 0.95f);
            }
        }

        foreach (var key in Profile.HabitSignals.Keys.ToList())
        {
            Profile.HabitSignals[key] = Math.Clamp(Profile.HabitSignals[key] - 0.005f, 0.05f, 0.95f);
        }
    }

    private List<string> _extractGoals(string message)
    {
        var goals = new List<string>();
        var goalPatterns = new[]
        {
            "i want to", "i need to", "i have to", "goal is", "objective",
            "我想", "我要", "我需要", "目标是", "必须", "打算"
        };

        var text = message.ToLowerInvariant();
        foreach (var pattern in goalPatterns)
        {
            var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var snippet = message.Substring(idx, Math.Min(message.Length - idx, 40));
                goals.Add(snippet.Trim());
            }
        }

        return goals;
    }

    private List<string> _extractImplicitWants(string message)
    {
        var wants = new List<string>();
        var wantPatterns = new[]
        {
            "can you", "could you", "would you", "please",
            "可以", "能不能", "能否", "帮我", "麻烦"
        };

        var text = message.ToLowerInvariant();
        foreach (var pattern in wantPatterns)
        {
            var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var snippet = message.Substring(idx, Math.Min(message.Length - idx, 40));
                wants.Add(snippet.Trim());
            }
        }

        return wants;
    }

    private void MarkDirty()
    {
        _synced = false;
        Save();
    }

    private void Save()
    {
        if (_synced) return;

        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(Profile, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(UserModelFile, json, Encoding.UTF8);
            _synced = true;
        }
        catch { /* non-fatal */ }
    }

    private static UserProfile Load()
    {
        try
        {
            if (File.Exists(UserModelFile))
            {
                var json = File.ReadAllText(UserModelFile, Encoding.UTF8);
                var profile = JsonSerializer.Deserialize<UserProfile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (profile is not null)
                    return profile;
            }
        }
        catch { /* non-fatal */ }

        return new UserProfile(
            Corrections: [],
            Habits: [],
            DomainAffinity: [],
            PreferredModel: "",
            VerbosityAvg: 0,
            PeakHour: 0,
            NegationRatio: 0f,
            ProjectContext: "",
            LastUpdated: (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds,
            Traits: new UserTraitVector(),
            HabitSignals: new Dictionary<string, float>
            {
                ["DeepDive"] = 0.3f,
                ["QuickScan"] = 0.3f,
                ["VisualPreference"] = 0.3f,
                ["CodeFirst"] = 0.3f,
                ["StepByStep"] = 0.3f,
                ["WhatsNew"] = 0.3f,
                ["GetItDone"] = 0.3f
            },
            BeliefState: new UserBeliefState([], [], [], [], 0f, 0f, "medium"),
            KnowledgeGaps: [],
            Expectation: new ExpectationModel("respond", "conversational", "standard", 0.3f, ""),
            EmpathySignal: new EmpathySignal("neutral", "", 0f, 0.3f, 0.1f, "neutral", "general_assistance")
        );
    }
}
