using System.Runtime.CompilerServices;
using LTAI.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Clients;

/// <summary>
/// MAF-aligned <see cref="IChatClient"/> middleware that filters <see cref="ChatOptions.Tools"/>
/// via semantic retrieval before the LLM call.
///
/// Replaces the former <see cref="Tools.ToolRetrievalProvider"/> (<see cref="Microsoft.Agents.AI.AIContextProvider"/>)
/// which had an ordering conflict with <c>HarnessAgent</c>'s built-in providers
/// (FileAccessProvider, BackgroundAgentsProvider).
///
/// As a <see cref="IChatClient"/> decorator, this runs AFTER all <see cref="Microsoft.Agents.AI.AIContextProvider"/>s
/// have merged their tools, so the full tool list is available for filtering.
/// <see cref="FunctionInvokingChatClient"/> keeps its own <see cref="ChatOptions"/> reference
/// for tool invocation; only the LLM sees the filtered subset.
/// </summary>
public sealed class ToolFilteringChatClient : IChatClient
{
    private static readonly HashSet<string> PinnedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReadFileContent", "RunCommand", "ListFiles", "GetCurrentDateTime",
    };

    private const int DefaultTopK = 8;

    private readonly IChatClient _inner;
    private readonly EmbeddingClient _embedder;
    private readonly ToolEmbeddingCache? _cache;

    public ToolFilteringChatClient(IChatClient inner, EmbeddingClient embedder, ToolEmbeddingCache? cache = null)
    {
        _inner = inner;
        _embedder = embedder;
        _cache = cache;
    }

    public void Dispose() => _inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var filteredOptions = await FilterToolsAsync(messages, options, cancellationToken).ConfigureAwait(false);
        return await _inner.GetResponseAsync(messages, filteredOptions, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filteredOptions = await FilterToolsAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, filteredOptions, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
    {
        if (serviceType is null) return null;
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);
    }

    private async ValueTask<ChatOptions?> FilterToolsAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        if (options?.Tools is null || options.Tools.Count == 0)
            return options;

        var tools = options.Tools.ToList();

        if (!ToolRegistry.IsInitialized)
        {
            await ToolRegistry.InitializeAsync(tools, _embedder, _cache, ct).ConfigureAwait(false);
        }

        var query = GetLastUserQuery(messages);
        List<AITool> selectedTools;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var hits = await ToolRegistry.SearchTopKAsync(query, _embedder, domain: null, DefaultTopK, ct)
                .ConfigureAwait(false);
            var hitNames = new HashSet<string>(hits.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);

            selectedTools = tools
                .Where(t => hitNames.Contains(t.Name ?? "") || PinnedTools.Contains(t.Name ?? ""))
                .ToList();
        }
        else
        {
            selectedTools = tools;
        }

        if (selectedTools.Count < 3)
        {
            selectedTools = tools.Where(t => PinnedTools.Contains(t.Name ?? "")).ToList();
            selectedTools.AddRange(tools.Where(t => !PinnedTools.Contains(t.Name ?? "")).Take(Math.Max(0, DefaultTopK - selectedTools.Count)));
        }

        var clone = options.Clone();
        clone.Tools = selectedTools;
        return clone;
    }

    private static string GetLastUserQuery(IEnumerable<ChatMessage> messages)
    {
        var parts = new List<string>(2);
        foreach (var m in messages.Reverse())
        {
            if (m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            {
                parts.Add(m.Text.Trim());
                if (parts.Count >= 2) break;
            }
        }
        parts.Reverse();
        return string.Join(" ", parts);
    }
}
