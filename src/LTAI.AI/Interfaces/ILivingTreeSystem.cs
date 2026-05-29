using LTAI.AI.Governors;
using LTAI.Core.Execution;
using LTAI.DNA;
using Microsoft.Extensions.AI;

namespace LTAI.AI.Interfaces;

/// <summary>
/// Core LTAI system interface — the primary entry point for agent conversations.
/// Orchestrates the full governor pipeline (Input → Context → Routing → L0/L1/L2)
/// and delegates final LLM calls to the configured IChatClient provider.
/// Implemented by LivingTreeSystem.
/// Callers: LTAI.Agent.MAF.AgenticLoop, LTAI.Web.SseAgentEndpoints, LTAI.Cli.
/// </summary>
public interface ILivingTreeSystem
{
    LTAI.Models.SystemMode Mode { get; }
    bool DNAEnabled { get; }
    IChatClient LLMClient { get; }

    SystemGuardian Guardian { get; }
    DNAStatus? DNAStatus { get; }
    InputGovernor InputGovernor { get; }
    ContextGovernor ContextGovernor { get; }
    RoutingGovernor RoutingGovernor { get; }
    TaskPipeline TaskPipeline { get; }

    /// <summary>Initialize all governor pipelines and load bootstrapping state.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>Full agent chat: governor pipeline → LLM → output governor → return.</summary>
    Task<string> ChatAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Streaming chat with step-by-step governor output via SSE-compatible IAsyncEnumerable.</summary>
    IAsyncEnumerable<string> StreamChatAsync(string query, string? modelOverride = null, CancellationToken cancellationToken = default);
    /// <summary>Streaming chat with explicit model override (e.g., flash vs deep).</summary>
    IAsyncEnumerable<string> StreamWithModelAsync(string query, string model, CancellationToken cancellationToken = default);
    /// <summary>Process a typed GovernorInput through the full pipeline and return GovernorOutput.</summary>
    Task<GovernorOutput> ProcessTypedAsync(GovernorInput input, CancellationToken cancellationToken = default);
}
