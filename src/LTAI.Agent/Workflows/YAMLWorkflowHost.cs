// Copyright (c) LTAI. All rights reserved.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Agents.AI.Workflows.Declarative.Events;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Workflows;

/// <summary>
/// Loads and executes MAF <c>Workflows.Declarative</c> YAML files for the LTAI
/// fast-path / chitchat / small-talk use cases. Replaces the legacy
/// <c>GreetingClassifier</c> C# implementation (P7.5) with declarative workflows.
/// </summary>
/// <remarks>
/// <para>
/// YAML files live in <c>src/LTAI.Agent/Workflows/ltai-workflows/*.yaml</c>
/// and are copied to the output directory at build time
/// (<c>CopyToOutputDirectory=PreserveNewest</c>).
/// </para>
/// <para>
/// As of P16.5, the monolithic <c>greeting.yaml</c> has been split into five
/// independent workflows tried in order: greeting → thanks → farewell → probing → test.
/// Each is compiled once (thread-safe) and cached for the lifetime of the process.
/// The first producing a <see cref="MessageActivityEvent"/> with non-empty text wins.
/// Users can edit individual YAML files without recompiling, and the P15 watcher
/// reloads them automatically.
/// </para>
/// </remarks>
public static class YAMLWorkflowHost
{
    // File names (without extension). Tried in order; first match wins.
    public static readonly string[] GreetingWorkflowNames =
        ["greeting", "thanks", "farewell", "probing", "test"];

    private static readonly object s_lock = new();
    private static Dictionary<string, Workflow>? s_workflows;
    private static IMcpToolHandler? s_mcpToolHandler;

    /// <summary>
    /// Run the greeting fast-path YAML workflows in priority order.
    /// Returns the canned reply from the first matching workflow, or <c>null</c>
    /// (caller should fall through to the LLM handoff).
    /// </summary>
    public static async Task<string?> RunGreetingFastPathAsync(string input, CancellationToken ct = default)
    {
        var workflows = GetOrBuildAll();
        foreach (var name in GreetingWorkflowNames)
        {
            ct.ThrowIfCancellationRequested();
            if (!workflows.TryGetValue(name, out var workflow))
                continue;

            var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: ct)
                .ConfigureAwait(false);

            await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
            {
                if (evt is MessageActivityEvent mae && !string.IsNullOrWhiteSpace(mae.Message))
                {
                    return mae.Message;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Diagnostic variant that returns the ordered list of event type names observed
    /// across ALL workflows. Used by the <c>ltai greeting-smoke</c> CLI subcommand.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunGreetingDiagnosticAsync(string input, CancellationToken ct = default)
    {
        var workflows = GetOrBuildAll();
        var events = new List<string>();
        foreach (var name in GreetingWorkflowNames)
        {
            ct.ThrowIfCancellationRequested();
            if (!workflows.TryGetValue(name, out var workflow))
                continue;

            var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: ct)
                .ConfigureAwait(false);

            await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
            {
                events.Add(evt.GetType().Name);
                // Stop at first SendActivity across all workflows
                if (evt is MessageActivityEvent mae && !string.IsNullOrWhiteSpace(mae.Message))
                {
                    return events;
                }
            }
        }
        return events;
    }

    private static Dictionary<string, Workflow> GetOrBuildAll()
    {
        if (s_workflows is not null) return s_workflows;
        lock (s_lock)
        {
            if (s_workflows is not null) return s_workflows;
            var options = new DeclarativeWorkflowOptions(new NoOpAgentProvider())
            {
                McpToolHandler = s_mcpToolHandler,
            };
            var map = new Dictionary<string, Workflow>(GreetingWorkflowNames.Length);
            foreach (var name in GreetingWorkflowNames)
            {
                var yamlPath = ResolveYamlPath(name);
                if (yamlPath is null) continue;
                map[name] = DeclarativeWorkflowBuilder.Build<string>(yamlPath, options);
            }
            s_workflows = map;
            return s_workflows;
        }
    }

    /// <summary>
    /// Resolve a YAML file path by name (without extension).
    /// Checks the embedded resource folder first, then flat base directory.
    /// Returns <c>null</c> if neither exists.
    /// </summary>
    private static string? ResolveYamlPath(string name)
    {
        var subdir = Path.Combine(AppContext.BaseDirectory, "LTAI.Agent.Workflows.ltai-workflows", $"{name}.yaml");
        if (File.Exists(subdir)) return subdir;

        var flat = Path.Combine(AppContext.BaseDirectory, $"{name}.yaml");
        if (File.Exists(flat)) return flat;

        return null;
    }

    /// <summary>
    /// P14.7: inject the MCP tool handler used by declarative workflows that
    /// call <c>InvokeMcpTool</c> actions. Called once at startup.
    /// Safe to call with <c>null</c> to disable MCP support for fast-path workflows.
    /// Invalidates all cached workflows so the next call rebuilds with the new handler.
    /// </summary>
    public static void ConfigureMcpToolHandler(IMcpToolHandler? handler)
    {
        s_mcpToolHandler = handler;
        lock (s_lock)
        {
            s_workflows = null;
        }
    }

    /// <summary>
    /// Stub <see cref="ResponseAgentProvider"/> for YAML workflows that do NOT
    /// call <c>InvokeAzureAgent</c> (e.g. fast-path workflows use only
    /// <c>SetVariable</c>, <c>ConditionGroup</c>, <c>SendActivity</c>). All
    /// agent-related operations throw because the workflow should never reach
    /// them; if it does, that's a YAML authoring bug worth surfacing loudly.
    /// </summary>
    private sealed class NoOpAgentProvider : ResponseAgentProvider
    {
        public override Task<string> CreateConversationAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "Fast-path workflows do not declare any InvokeAzureAgent actions; " +
                "ResponseAgentProvider.CreateConversationAsync should not be reached.");

        public override Task<ChatMessage> CreateMessageAsync(string conversationId, ChatMessage conversationMessage, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "Fast-path workflows do not declare any InvokeAzureAgent actions.");

        public override Task<ChatMessage> GetMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "Fast-path workflows do not declare any InvokeAzureAgent actions.");

        public override IAsyncEnumerable<AgentResponseUpdate> InvokeAgentAsync(
            string agentId, string? agentVersion, string? conversationId,
            IEnumerable<ChatMessage>? messages, IDictionary<string, object?>? inputArguments,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                $"Fast-path workflow attempted to invoke agent '{agentId}', but " +
                "these workflows should only use SetVariable / ConditionGroup / SendActivity. " +
                "Check the YAML for an InvokeAzureAgent action that shouldn't be there.");

        public override IAsyncEnumerable<ChatMessage> GetMessagesAsync(
            string conversationId, int? limit = null, string? after = null,
            string? before = null, bool newestFirst = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "Fast-path workflows do not declare any InvokeAzureAgent actions.");
    }
}
