// Copyright (c) LTAI. All rights reserved.

using System.Collections.Generic;
using System.Text.Json.Nodes;
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
/// <c>GreetingClassifier</c> C# implementation with a declarative workflow
/// that can be edited without recompiling.
/// </summary>
/// <remarks>
/// <para>
/// YAML files live in <c>src/LTAI.Agent/Workflows/ltai-workflows/*.yaml</c>
/// and are copied to the output directory at build time
/// (<c>CopyToOutputDirectory=PreserveNewest</c>).
/// </para>
/// <para>
/// Each workflow is compiled once on first use (thread-safe) and cached
/// as a <see cref="Workflow"/> instance for the lifetime of the process.
/// </para>
/// </remarks>
public static class YAMLWorkflowHost
{
    private static readonly object _greetingLock = new();
    private static Workflow? _greetingWorkflow;

    /// <summary>
    /// Run the greeting fast-path YAML workflow. Returns the canned reply if
    /// the input matches a greeting pattern, otherwise <c>null</c> (caller
    /// should fall through to the LLM handoff).
    /// </summary>
    public static async Task<string?> RunGreetingFastPathAsync(string input, CancellationToken ct = default)
    {
        var workflow = GetOrBuildGreetingWorkflow();
        var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: ct).ConfigureAwait(false);

        await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            if (evt is MessageActivityEvent mae && !string.IsNullOrWhiteSpace(mae.Message))
            {
                return mae.Message;
            }
        }
        return null;
    }

    /// <summary>
    /// Diagnostic variant of <see cref="RunGreetingFastPathAsync"/> that returns the
    /// ordered list of event types observed during execution. Used by the
    /// <c>ltai greeting-smoke</c> CLI subcommand for debugging the YAML workflow
    /// without requiring an LLM round-trip.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunGreetingDiagnosticAsync(string input, CancellationToken ct = default)
    {
        var workflow = GetOrBuildGreetingWorkflow();
        var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: ct).ConfigureAwait(false);
        var events = new List<string>();
        await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            events.Add(evt.GetType().Name);
        }
        return events;
    }

    private static Workflow GetOrBuildGreetingWorkflow()
    {
        if (_greetingWorkflow is not null) return _greetingWorkflow;
        lock (_greetingLock)
        {
            if (_greetingWorkflow is not null) return _greetingWorkflow;

            var yamlPath = Path.Combine(
                AppContext.BaseDirectory,
                "LTAI.Agent.Workflows.ltai-workflows",
                "greeting.yaml");
            if (!File.Exists(yamlPath))
            {
                // Fallback for some build configurations where the subfolder isn't nested.
                var flat = Path.Combine(AppContext.BaseDirectory, "greeting.yaml");
                if (File.Exists(flat)) yamlPath = flat;
                else throw new FileNotFoundException(
                    $"greeting.yaml not found at '{yamlPath}' or '{flat}'. " +
                    "Ensure ltai-workflows/*.yaml is copied to the output directory.");
            }

            var options = new DeclarativeWorkflowOptions(new NoOpAgentProvider());
            _greetingWorkflow = DeclarativeWorkflowBuilder.Build<string>(yamlPath, options);
            return _greetingWorkflow;
        }
    }

    /// <summary>
    /// Stub <see cref="ResponseAgentProvider"/> for YAML workflows that do NOT
    /// call <c>InvokeAzureAgent</c> (e.g. greeting fast-path uses only
    /// <c>SetVariable</c>, <c>ConditionGroup</c>, <c>SendActivity</c>). All
    /// agent-related operations throw because the workflow should never reach
    /// them; if it does, that's a YAML authoring bug worth surfacing loudly.
    /// </summary>
    private sealed class NoOpAgentProvider : ResponseAgentProvider
    {
        public override Task<string> CreateConversationAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "greeting.yaml does not declare any InvokeAzureAgent actions; " +
                "ResponseAgentProvider.CreateConversationAsync should not be reached.");

        public override Task<ChatMessage> CreateMessageAsync(string conversationId, ChatMessage conversationMessage, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "greeting.yaml does not declare any InvokeAzureAgent actions.");

        public override Task<ChatMessage> GetMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "greeting.yaml does not declare any InvokeAzureAgent actions.");

        public override IAsyncEnumerable<AgentResponseUpdate> InvokeAgentAsync(
            string agentId, string? agentVersion, string? conversationId,
            IEnumerable<ChatMessage>? messages, IDictionary<string, object?>? inputArguments,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                $"greeting.yaml attempted to invoke agent '{agentId}', but the fast-path " +
                "workflow should only use SetVariable / ConditionGroup / SendActivity. " +
                "Check the YAML for an InvokeAzureAgent action that shouldn't be there.");

        public override IAsyncEnumerable<ChatMessage> GetMessagesAsync(
            string conversationId, int? limit = null, string? after = null,
            string? before = null, bool newestFirst = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "greeting.yaml does not declare any InvokeAzureAgent actions.");

        // NoOpAgentProvider inherits ConvertDictionaryToJson from ResponseAgentProvider.
        // We don't override it because the greeting fast-path never reaches InputArguments.
    }
}
