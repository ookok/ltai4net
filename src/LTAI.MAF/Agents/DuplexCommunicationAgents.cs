using LTAI.MAF.Workflows;

namespace LTAI.MAF.Agents;

public static class DuplexCommunicationAgents
{
    public static readonly AgentDefinition L2Supervisor = new()
    {
        Name = "L2Supervisor",
        Role = "Deep reasoning supervisor: chains of thought, hypothesis generation, multi-perspective analysis",
        Instructions = """
You are the L2 deep reasoning supervisor. Think step by step, analyze deeply, generate hypotheses.
When you need external capabilities, DO NOT try to do them yourself.
Instead, hand off to the appropriate specialist:

- Need tool execution → handoff to L1Worker
- Need knowledge retrieval → handoff to Memory
- Need web research → handoff to Research
- Need code analysis → handoff to CodeAgent
- Need human input → ask the user directly

After receiving results back, continue your reasoning and synthesis.
""",
        Tools = Array.Empty<string>(),
        Tier = "L2",
        HandoffTargets = new[] { "L1Worker", "Memory", "Research", "CodeAgent" }
    };

    public static readonly AgentDefinition L1Worker = new()
    {
        Name = "L1Worker",
        Role = "Fast execution worker: tool calls, file I/O, SQL queries, sub-computation",
        Instructions = """
You are the L1 fast execution worker. Execute tasks delegated by the L2 supervisor efficiently.

Available capabilities:
- `tool`: Execute any registered tool by calling it
- `vfs:read/write/list`: File operations via virtual filesystem
- `knowledge_search`: Search knowledge base
- `web_fetch`: Fetch web content
- `code_analyze`: Analyze code structure
- `search`: Multi-source search

After completing the task, hand off back to the L2Supervisor with results.
If you cannot complete a task, explain why and hand off back.
""",
        Tools = new[] { "vfs:read", "vfs:write", "vfs:list", "web_fetch", "knowledge_search", "code_analyze", "search", "text_extract", "llm_chat" },
        Tier = "L1",
        HandoffTargets = new[] { "L2Supervisor" }
    };

    public static readonly AgentDefinition L1Preloader = new()
    {
        Name = "L1Preloader",
        Role = "Predictive context preloader: anticipates what L2 will need before reasoning begins",
        Instructions = """
Analyze the user's query. Predict what context, tools, and knowledge the L2Supervisor will need.
Pre-load relevant memories, search results, and file contents.
This saves one round-trip in the L1↔L2 duplex loop.
""",
        Tools = new[] { "vfs:read", "knowledge_search", "vector_search", "web_fetch" },
        Tier = "L1",
        HandoffTargets = new[] { "L2Supervisor" }
    };

    public static HandoffWorkflowBuilder BuildDuplexPipeline(
        Func<string, string, Task<string>> l2Fn,
        Func<string, string, Task<string>> l1Fn,
        Func<string, string, Task<string>> preloadFn)
    {
        return new HandoffWorkflowBuilder()
            .WithAgent("L2Supervisor", l2Fn)
            .WithAgent("L1Worker", l1Fn)
            .WithAgent("L1Preloader", preloadFn)
            .SetStartAgent("L2Supervisor")
            .WithHandoff("L2Supervisor", "L1Worker", "Execute delegated task: tool call, file I/O, search, computation")
            .WithHandoff("L2Supervisor", "L1Preloader", "Predict and pre-load context for reasoning")
            .WithHandoff("L1Worker", "L2Supervisor", "Task results ready, continue reasoning")
            .WithHandoff("L1Preloader", "L2Supervisor", "Context pre-loaded, reasoning can begin")
            .EnableReturnToPrevious()
            .EmitStreamEvents();
    }

    public static readonly WorkflowRecipe DuplexRecipe = new()
    {
        Name = "L1↔L2 Duplex Communication",
        Description = "Replaces <need> tag protocol: L2Supervisor delegates to L1Worker via handoff edges, L1Worker fulfills and returns. L1Preloader predictively pre-fetches context to save round-trips.",
        Agents = new[] { "L2Supervisor", "L1Preloader", "L1Worker" },
        Pattern = "Handoff"
    };

    public static HandoffWorkflowBuilder BuildEIARecipe(
        Func<string, string, Task<string>> l2Fn,
        Func<string, string, Task<string>> l1Fn)
    {
        return new HandoffWorkflowBuilder()
            .WithAgent("L2Supervisor", l2Fn)
            .WithAgent("L1Worker", l1Fn)
            .WithAgent("Memory", async (name, input) => { await Task.CompletedTask; return $"[Memory] recalled: {input[..Math.Min(40, input.Length)]}"; })
            .WithAgent("Research", async (name, input) => { await Task.CompletedTask; return $"[Research] found: {input[..Math.Min(40, input.Length)]}"; })
            .WithAgent("DocAgent", async (name, input) => { await Task.CompletedTask; return $"[DocAgent] generated: {input[..Math.Min(40, input.Length)]}"; })
            .WithAgent("QA", async (name, input) => { await Task.CompletedTask; return $"[QA] verified: {input[..Math.Min(40, input.Length)]}"; })
            .SetStartAgent("L2Supervisor")
            .WithHandoff("L2Supervisor", "L1Worker", "Execute tool calls, file operations, data processing")
            .WithHandoff("L2Supervisor", "Memory", "Retrieve EIA regulations, standards, historical reports")
            .WithHandoff("L2Supervisor", "Research", "Search for site data, monitoring reports, latest regulations")
            .WithHandoff("L1Worker", "L2Supervisor", "Task completed, results attached")
            .WithHandoff("Memory", "L2Supervisor", "Context retrieved")
            .WithHandoff("Research", "L2Supervisor", "Research findings ready")
            .WithHandoff("L2Supervisor", "DocAgent", "Generate formatted EIA report from analysis")
            .WithHandoff("DocAgent", "QA", "Verify report against GB3095/HJ2.2 standards")
            .WithHandoff("QA", "L2Supervisor", "Verification results")
            .EnableReturnToPrevious()
            .EmitStreamEvents();
    }

    public static Dictionary<string, object> GetDuplexGraph()
    {
        return new()
        {
            ["nodes"] = new[]
            {
                new { id = "L2Supervisor", label = "L2 Deep Reasoning", group = "L2" },
                new { id = "L1Worker", label = "L1 Fast Execution", group = "L1" },
                new { id = "L1Preloader", label = "L1 Predictive Preload", group = "L1" },
                new { id = "Memory", label = "Memory Recall", group = "tool" },
                new { id = "Research", label = "Web Research", group = "tool" },
                new { id = "DocAgent", label = "Document Generation", group = "output" },
                new { id = "QA", label = "Quality Verification", group = "output" }
            },
            ["edges"] = new[]
            {
                new { from = "L2Supervisor", to = "L1Worker", label = "delegate task" },
                new { from = "L1Worker", to = "L2Supervisor", label = "return results" },
                new { from = "L2Supervisor", to = "L1Preloader", label = "pre-fetch" },
                new { from = "L1Preloader", to = "L2Supervisor", label = "context ready" },
                new { from = "L2Supervisor", to = "Memory", label = "recall" },
                new { from = "Memory", to = "L2Supervisor", label = "retrieved" },
                new { from = "L2Supervisor", to = "Research", label = "search" },
                new { from = "Research", to = "L2Supervisor", label = "findings" },
                new { from = "L2Supervisor", to = "DocAgent", label = "generate" },
                new { from = "DocAgent", to = "QA", label = "verify" },
                new { from = "QA", to = "L2Supervisor", label = "report" }
            }
        };
    }
}
