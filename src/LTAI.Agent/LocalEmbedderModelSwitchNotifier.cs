// Copyright (c) LTAI. All rights reserved.

using LTAI.AI;

namespace LTAI.Agent;

/// <summary>
/// P14.8: bridges <see cref="LocalEmbedder.ModelSwitched"/> to the static
/// in-memory vector caches in <see cref="AgentRegistry"/> and
/// <see cref="ToolRegistry"/>. Created by DI in
/// <c>ServiceCollectionExtensions.AddLTAIAgent</c>; the local embedder
/// invalidates <see cref="ToolEmbeddingCache"/> itself, so this service
/// only needs to clear the static registries that live in LTAI.Agent.
/// </summary>
public sealed class LocalEmbedderModelSwitchNotifier
{
    public LocalEmbedderModelSwitchNotifier(LocalEmbedder? local)
    {
        if (local == null) return;
        local.ModelSwitched += _ =>
        {
            AgentRegistry.ClearEmbeddings();
            ToolRegistry.ClearEmbeddings();
        };
    }
}
