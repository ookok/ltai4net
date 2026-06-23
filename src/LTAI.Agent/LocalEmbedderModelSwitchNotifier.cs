using LTAI.AI;

namespace LTAI.Agent;

/// <summary>
/// P14.8: bridges <see cref="LocalEmbedder.ModelSwitched"/> to the static
/// in-memory vector caches in <see cref="AgentRegistry"/> and
/// <see cref="IToolRegistry"/>. Created by DI in
/// <c>ServiceCollectionExtensions.AddLTAIAgent</c>; the local embedder
/// invalidates <see cref="ToolEmbeddingCache"/> itself, so this service
/// only needs to clear the static registries that live in LTAI.Agent.
/// </summary>
public sealed class LocalEmbedderModelSwitchNotifier
{
    private readonly IToolRegistry _toolRegistry;

    /// <summary>P1: Tracks the last model switch for ChatAgent to surface as notification.</summary>
    public static string? LastSwitchMessage { get; private set; }

    public LocalEmbedderModelSwitchNotifier(IToolRegistry toolRegistry, LocalEmbedder? local = null)
    {
        _toolRegistry = toolRegistry;
        if (local == null) return;
        local.ModelSwitched += modelName =>
        {
            AgentRegistry.ClearEmbeddings();
            _toolRegistry.ClearEmbeddings();
            LastSwitchMessage = $"🔄 Embedding model switched to '{modelName}'. " +
                "Routing behavior may differ from previous model.";
        };
    }

    /// <summary>Consume and clear the switch notification.</summary>
    public static string? ConsumeSwitchMessage()
    {
        var msg = LastSwitchMessage;
        LastSwitchMessage = null;
        return msg;
    }
}
