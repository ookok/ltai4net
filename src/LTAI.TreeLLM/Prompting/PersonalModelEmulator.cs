using System.Text;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;

namespace LTAI.TreeLLM.Prompting;

public sealed class PersonalModelEmulator
{
    private readonly AgenticRAG _agenticRAG;
    private readonly StructMemory _structMemory;
    private readonly PromptBuilder _promptBuilder;

    public PersonalModelEmulator(
        AgenticRAG agenticRAG,
        StructMemory structMemory,
        PromptBuilder promptBuilder)
    {
        _agenticRAG = agenticRAG;
        _structMemory = structMemory;
        _promptBuilder = promptBuilder;
    }

    public async Task<string> BuildPersonalSystemPrompt(
        string sessionId,
        string currentQuery,
        PersonalModelOptions? options = null)
    {
        var opts = options ?? new PersonalModelOptions();
        var parts = new List<string>();

        parts.Add(BuildIdentityBlock(opts));

        var preferenceBlock = await BuildPreferenceBlock(sessionId, currentQuery, opts);
        if (!string.IsNullOrEmpty(preferenceBlock))
            parts.Add(preferenceBlock);

        var knowledgeBlock = BuildKnowledgeBlock(currentQuery, opts);
        if (!string.IsNullOrEmpty(knowledgeBlock))
            parts.Add(knowledgeBlock);

        var behavioralBlock = BuildBehavioralBlock(opts);
        if (!string.IsNullOrEmpty(behavioralBlock))
            parts.Add(behavioralBlock);

        var memoryBlock = await BuildMemoryBlock(sessionId, currentQuery, opts);
        if (!string.IsNullOrEmpty(memoryBlock))
            parts.Add(memoryBlock);

        if (opts.IncludeSecondMeInstructions)
            parts.Add(BuildSecondMeInstructions(opts));

        return string.Join("\n\n", parts);
    }

    public async Task<PersonalContextResult> EnrichWithPersonalContext(
        string sessionId,
        string question,
        PromptBuildOptions promptOpts,
        PersonalModelOptions? personalOpts = null)
    {
        var perOpts = personalOpts ?? new PersonalModelOptions();

        var personalSystemPrompt = await BuildPersonalSystemPrompt(sessionId, question, perOpts);
        promptOpts.SessionContext = (promptOpts.SessionContext ?? "") + "\n" + personalSystemPrompt;

        var longTermDocs = _agenticRAG.Search(question, RAGMode.Iterative,
            domain: promptOpts.Domain ?? "general");

        var (sysPrompt, userPrompt) = await _promptBuilder.BuildPrompt(question, longTermDocs, promptOpts);

        return new PersonalContextResult
        {
            SystemPrompt = sysPrompt,
            UserPrompt = userPrompt,
            PersonalIdentityBlock = BuildIdentityBlock(perOpts),
            PreferenceCount = perOpts.Identity.Facts?.Count ?? 0
        };
    }

    private static string BuildIdentityBlock(PersonalModelOptions opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 个人身份模型 (L2 Emulation)");

        if (!string.IsNullOrEmpty(opts.Identity.Name))
            sb.AppendLine($"- 身份: {opts.Identity.Name}");
        if (!string.IsNullOrEmpty(opts.Identity.Role))
            sb.AppendLine($"- 角色: {opts.Identity.Role}");
        if (opts.Identity.Facts is { Count: > 0 })
        {
            sb.AppendLine("- 已知事实:");
            foreach (var fact in opts.Identity.Facts.Take(opts.MaxIdentityFacts))
                sb.AppendLine($"  - {fact}");
        }
        if (opts.Identity.Domains is { Count: > 0 })
            sb.AppendLine($"- 专业领域: {string.Join(", ", opts.Identity.Domains)}");

        return sb.ToString();
    }

    private async Task<string> BuildPreferenceBlock(
        string sessionId, string query, PersonalModelOptions opts)
    {
        var preferences = _agenticRAG.Search(query, domain: "preferences", maxRounds: 1);
        if (preferences.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("## 个人偏好模型");

        foreach (var pref in preferences.Take(opts.MaxPreferenceItems))
        {
            var summary = PromptBuilder.HeuristicSummarize(pref.Content, 200);
            sb.AppendLine($"- {summary}");
        }

        return sb.ToString();
    }

    private string BuildKnowledgeBlock(string query, PersonalModelOptions opts)
    {
        if (!opts.IncludeDomainKnowledge) return "";

        var knowledgeDocs = _agenticRAG.Search(query, domain: "domain_knowledge", maxRounds: 2);
        if (knowledgeDocs.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("## 领域知识模型");

        foreach (var doc in knowledgeDocs.Take(opts.MaxKnowledgeItems))
        {
            var summary = PromptBuilder.HeuristicSummarize(doc.Content, 300);
            sb.AppendLine($"- [{doc.Domain ?? "general"}] {summary}");
        }

        return sb.ToString();
    }

    private static string BuildBehavioralBlock(PersonalModelOptions opts)
    {
        if (!opts.IncludeBehavioralProfile) return "";

        var sb = new StringBuilder();
        sb.AppendLine("## 行为模型");
        sb.AppendLine($"- 沟通风格: {opts.Behavioral.CommunicationStyle}");
        sb.AppendLine($"- 决策偏好: {opts.Behavioral.DecisionPreference}");
        sb.AppendLine($"- 风险态度: {opts.Behavioral.RiskAttitude}");
        sb.AppendLine($"- 详细程度: {opts.Behavioral.DetailLevel}");

        return sb.ToString();
    }

    private async Task<string> BuildMemoryBlock(
        string sessionId, string query, PersonalModelOptions opts)
    {
        if (!opts.IncludeEpisodicMemory) return "";

        try
        {
            var (memoryEvents, _) = await _structMemory.RetrieveForQuery(query);
            if (memoryEvents.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("## 情境记忆");

            foreach (var evt in memoryEvents.Take(opts.MaxMemoryEvents))
            {
                var content = evt.TextForRetrieval();
                if (content.Length > 150)
                    content = content[..150] + "...";
                sb.AppendLine($"- [{evt.Role}] {content}");
            }

            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string BuildSecondMeInstructions(PersonalModelOptions opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 上下文提供者模式 (Context Provider Role)");
        sb.AppendLine("你正在扮演用户的'第二自我' (Second Me)。");
        sb.AppendLine("你的角色是*上下文提供者*，不是任务执行者：");
        sb.AppendLine("1. 利用上述身份模型理解用户背景和偏好");
        sb.AppendLine("2. 利用情境记忆理解对话历史和上下文");
        sb.AppendLine("3. 利用领域知识模型提供准确的领域信息");
        sb.AppendLine("4. 综合所有信息，以符合用户行为模型的方式回应");

        if (opts.Mode == ProviderMode.ThirdParty)
            sb.AppendLine("5. 在代表用户与第三方交互时，保持用户的立场、偏好和身份一致性");

        return sb.ToString();
    }
}

public sealed class PersonalModelOptions
{
    public IdentitySnapshot Identity { get; set; } = new();
    public BehavioralSnapshot Behavioral { get; set; } = new();
    public int MaxIdentityFacts { get; set; } = 8;
    public int MaxPreferenceItems { get; set; } = 5;
    public int MaxKnowledgeItems { get; set; } = 5;
    public int MaxMemoryEvents { get; set; } = 5;
    public bool IncludeDomainKnowledge { get; set; } = true;
    public bool IncludeBehavioralProfile { get; set; } = true;
    public bool IncludeEpisodicMemory { get; set; } = true;
    public bool IncludeSecondMeInstructions { get; set; } = true;
    public ProviderMode Mode { get; set; } = ProviderMode.Self;
}

public sealed class IdentitySnapshot
{
    public string? Name { get; set; }
    public string? Role { get; set; }
    public List<string>? Facts { get; set; }
    public List<string>? Domains { get; set; }
}

public sealed class BehavioralSnapshot
{
    public string CommunicationStyle { get; set; } = "专业简洁";
    public string DecisionPreference { get; set; } = "数据驱动";
    public string RiskAttitude { get; set; } = "适度的";
    public string DetailLevel { get; set; } = "平衡";
}

public enum ProviderMode { Self, ThirdParty, Enhance, Critic }

public sealed record PersonalContextResult
{
    public string SystemPrompt { get; init; } = "";
    public string UserPrompt { get; init; } = "";
    public string PersonalIdentityBlock { get; init; } = "";
    public int PreferenceCount { get; init; }
}
