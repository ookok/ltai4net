namespace LTAI.MAF.Agents;

public sealed class AgentDefinition
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string[] Tools { get; set; } = Array.Empty<string>();
    public string[] Skills { get; set; } = Array.Empty<string>();
    public string[] HandoffTargets { get; set; } = Array.Empty<string>();
    public string Tier { get; set; } = "L1";
}

public static class AgentCatalog
{
    public static readonly AgentDefinition Router = new()
    {
        Name = "Router",
        Role = "Routes requests to the best model/provider based on task type, complexity, cost, and latency",
        Instructions = "Analyze the user request. Determine task type (code/reasoning/chat/search). Select optimal provider from available pool. Consider cost budget and latency requirements.",
        Tools = new[] { "vfs:read", "knowledge_search" },
        Tier = "L1",
        HandoffTargets = new[] { "Triage", "Executor", "Planner" }
    };

    public static readonly AgentDefinition Triage = new()
    {
        Name = "Triage",
        Role = "Classifies intent, detects emotion, predicts needs using spinal reflex and VAD analysis",
        Instructions = "Classify the user's intent (code/reasoning/chat/search/analysis). Detect emotional tone (Valence/Arousal/Dominance). Predict what tools/context the executor will need. Fast, sub-50ms reflex path for simple queries.",
        Tools = new[] { "vfs:read" },
        Tier = "L1",
        HandoffTargets = new[] { "Memory", "Executor", "Planner" }
    };

    public static readonly AgentDefinition Memory = new()
    {
        Name = "Memory",
        Role = "Retrieves relevant context from 5-tier MoE memory (Flash/Hot/Warm/Cold/Deep)",
        Instructions = "Retrieve relevant memories and context for the current query. Check Hot tier first (working memory), then Warm (recent), Cold (archived), Deep (permanent). Return enriched context.",
        Tools = new[] { "vfs:read", "vfs:list", "knowledge_search", "vector_search" },
        Tier = "L2",
        HandoffTargets = new[] { "Executor", "Planner", "Research" }
    };

    public static readonly AgentDefinition Planner = new()
    {
        Name = "Planner",
        Role = "Creates execution plans using diffusion/GTSM/task-tree decomposition",
        Instructions = "Decompose the user's request into a structured execution plan. Identify dependencies. Select appropriate tools for each step. Estimate costs and time. Return a numbered plan with agent assignments.",
        Tools = new[] { "vfs:read", "knowledge_search" },
        Tier = "L2",
        HandoffTargets = new[] { "Executor", "Research", "Memory" }
    };

    public static readonly AgentDefinition Executor = new()
    {
        Name = "Executor",
        Role = "Executes tasks using ReAct loop with tool calling, file operations, code execution",
        Instructions = "Execute the plan step by step. Use tools when appropriate. Read/write files via VFS. Call external APIs. Run code in sandbox when needed. Report results for each step with observations.",
        Tools = new[] { "vfs:read", "vfs:write", "vfs:list", "web_fetch", "code_analyze", "search", "doc_parse", "text_extract", "llm_chat" },
        Tier = "L2",
        HandoffTargets = new[] { "Critic", "QA", "Research", "CodeAgent", "DocAgent" }
    };

    public static readonly AgentDefinition Critic = new()
    {
        Name = "Critic",
        Role = "Reviews output for hallucinations, contradictions, incompleteness, and quality issues",
        Instructions = "Review the executor's output critically. Check for: factual accuracy, logical consistency, completeness, hallucination. Flag issues with PASS/FLAG: reason format. Suggest improvements when quality below threshold.",
        Tools = new[] { "vfs:read", "knowledge_search", "vector_search" },
        Tier = "L2",
        HandoffTargets = new[] { "Executor", "QA" }
    };

    public static readonly AgentDefinition QA = new()
    {
        Name = "QA",
        Role = "Verifies quality: metamorphic testing, golden trace comparison, scoring, compliance",
        Instructions = "Verify output quality. Run metamorphic tests. Compare against golden traces. Score on accuracy/completeness/clarity. Check regulatory compliance for EIA reports. Return pass/fail with detailed breakdown.",
        Tools = new[] { "vfs:read", "knowledge_search", "doc_parse" },
        Tier = "L2",
        HandoffTargets = new[] { "Executor", "Governance" }
    };

    public static readonly AgentDefinition Research = new()
    {
        Name = "Research",
        Role = "Gathers information: web search, knowledge foraging, API calls, document retrieval",
        Instructions = "Research the topic thoroughly. Search multiple sources. Forage knowledge from configured sites. Retrieve relevant documents. Synthesize findings into a structured brief. Cite all sources.",
        Tools = new[] { "web_fetch", "search", "knowledge_search", "vector_search", "vfs:read", "doc_parse" },
        Tier = "L2",
        HandoffTargets = new[] { "Executor", "DocAgent", "Planner" }
    };

    public static readonly AgentDefinition CodeAgent = new()
    {
        Name = "CodeAgent",
        Role = "Handles code generation, review, analysis, debugging across 13 languages",
        Instructions = "Generate clean, well-documented code. Review for bugs/security/performance. Analyze existing codebases. Debug issues with stack trace analysis. Use Roslyn for C#, TreeSitter for other languages.",
        Tools = new[] { "vfs:read", "vfs:write", "code_analyze", "web_fetch", "search" },
        Skills = new[] { "code-generation", "code-review" },
        Tier = "L2",
        HandoffTargets = new[] { "Executor", "Critic" }
    };

    public static readonly AgentDefinition DocAgent = new()
    {
        Name = "DocAgent",
        Role = "Processes documents: parsing, generation, formatting, EIA report production",
        Instructions = "Parse incoming documents (PDF/DOCX/XLSX/MD). Generate formatted reports with proper styles. For EIA: follow GB3095/HJ2.2 standards with correct headings, tables, citations. Apply style fingerprints from historical reports.",
        Tools = new[] { "vfs:read", "vfs:write", "doc_parse", "text_extract", "knowledge_search" },
        Skills = new[] { "eia-report" },
        Tier = "L2",
        HandoffTargets = new[] { "Executor", "QA" }
    };

    public static readonly AgentDefinition Governance = new()
    {
        Name = "Governance",
        Role = "Enforces runtime policies: blocks unsafe actions, audits tool calls, ensures compliance",
        Instructions = "Enforce runtime policies. Block destructive commands. Prevent credential leaks. Audit all model calls. Warn on suspicious patterns. Maintain immutable audit trail.",
        Tools = new[] { "vfs:read" },
        Tier = "System",
        HandoffTargets = new[] { "Executor" }
    };

    public static readonly AgentDefinition Reflection = new()
    {
        Name = "Reflection",
        Role = "Meta-cognition: self-improvement, strategy optimization, pattern discovery",
        Instructions = "Reflect on the conversation and execution. What worked well? What could be improved? Discover new patterns. Optimize routing strategies. Generate improvement suggestions for future runs.",
        Tools = new[] { "vfs:read", "vfs:write", "knowledge_search" },
        Tier = "L2",
        HandoffTargets = Array.Empty<string>()
    };

    public static Dictionary<string, AgentDefinition> GetAll() => new()
    {
        ["Router"] = Router, ["Triage"] = Triage, ["Memory"] = Memory,
        ["Planner"] = Planner, ["Executor"] = Executor, ["Critic"] = Critic,
        ["QA"] = QA, ["Research"] = Research, ["CodeAgent"] = CodeAgent,
        ["DocAgent"] = DocAgent, ["Governance"] = Governance, ["Reflection"] = Reflection
    };

    public static AgentDefinition Get(string name) => GetAll().GetValueOrDefault(name, Executor);
}
