using LTAI.Agent.Context;
using LTAI.Agent.LanguageServer;
using LTAI.Agent.Memory;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Core.Configuration;
using LTAI.Core.Safety;
using LTAI.Core.Specs;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#pragma warning disable MAAI001

namespace LTAI.Agent;

/// <summary>
/// Assembles the 7-layer memory palace + tool RAG + safety coordinator into the
/// ordered <see cref="AIContextProvider"/> array that <c>HarnessAgentOptions</c> consumes.
///
/// Layer order matters — the harness walks providers in this order on every turn:
///   [0] SkillRankingProvider      — Skill Evolution Engine ranking
///   [1] SafetyCoordinator         — only when <see cref="LTAIOptions.AI.SkipSafetyChecks"/> = false
///   [2] L0IdentityProvider        — always-loaded identity (~100t)
///   [3] L1EssentialProvider       — 5 most-recent essential memories
///   [4] CompactionProvider        — MAF pipeline compaction
///   [5] CCRProvider               — content compression/retrieval markers
///   [6] KbGraph / CgGraph / CodeChunkIndex / WasmtimeSandbox — on-demand
///   [7] L3OnDemandProvider        — task-relevant memory
///   [8] L4DeepSearchProvider      — semantic deep search
///   [9] L6AgentDiaryProvider      — diary entries
///  [10] ProvenanceProvider        — knowledge provenance tracking
///  [11] InstructionProvider       — per-model instruction hints
///  [12] EnvironmentProvider       — current cwd / OS / runtime
///  [13] skillsProvider            — skills directory contents
///  [14] CacheAlignerProvider      — KV-cache alignment hints
///  [15] LspDiagnosticsProvider    — LSP diagnostics
///
    /// Note: ToolRetrievalProvider (removed) was replaced by ToolFilteringChatClient
/// (a MAF IChatClient middleware) to avoid ordering conflicts with
/// HarnessAgent's built-in providers (FileAccessProvider, BackgroundAgentsProvider).
/// Tool filtering now runs at the IChatClient level, after all AIContextProviders
/// have merged their tools.
/// </summary>
internal static class AgentContextProviderBuilder
{
    public static AIContextProvider[] Build(IServiceProvider sp,
        ILoggerFactory loggerFactory, string name, string identityText,
        CompactionProvider compaction, KbGraph kbGraph, CgGraph codeGraph,
        LTAI.Agent.Indexing.CodeChunkIndex codeChunkIndex, WasmtimeSandbox wasmtimeSandbox,
        LTAI.AI.EmbeddingClient embedder, LTAI.Agent.Memory.PalaceStore palaceStore,
        string identityText2, string? modelId,
        Microsoft.Agents.AI.AgentSkillsProvider skillsProvider,
        SafetyCoordinator? safety)
    {
        var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
        var specSvc = new SpecService(opts.ResolveDataPath("specs"));

        var providers = new List<AIContextProvider>(18)
        {
            new SkillRankingProvider(
                sp.GetRequiredService<SkillEvolutionEngine>(),
                loggerFactory.CreateLogger<SkillRankingProvider>()),
            new MemoryAuthorityProvider(),
            new L0IdentityProvider(identityText),
            new L1EssentialProvider(palaceStore, name,
                sp.GetService<EntropyTracker>(),
                loggerFactory.CreateLogger<L1EssentialProvider>()),
            new SpecContextProvider(specSvc,
                loggerFactory.CreateLogger<SpecContextProvider>()),
            compaction,
            new CCRProvider(
                sp.GetRequiredService<CompressionStore>(),
                loggerFactory.CreateLogger<CCRProvider>()),
            kbGraph, codeGraph, codeChunkIndex, wasmtimeSandbox,
            new L3OnDemandProvider(palaceStore,
                sp.GetService<EntropyTracker>(),
                loggerFactory.CreateLogger<L3OnDemandProvider>()),
            new L4DeepSearchProvider(palaceStore, embedder,
                sp.GetService<EntropyTracker>(),
                loggerFactory.CreateLogger<L4DeepSearchProvider>()),
            new L6AgentDiaryProvider(palaceStore, name,
                loggerFactory.CreateLogger<L6AgentDiaryProvider>()),
            sp.GetRequiredService<LTAI.Agent.Indexing.ProvenanceProvider>(),
            new InstructionProvider(modelId),
            new EnvironmentProvider(), skillsProvider,
            new CacheAlignerProvider(
                loggerFactory.CreateLogger<CacheAlignerProvider>()),
            new LspDiagnosticsProvider(AgentBuilder.GetLspManager()),
        };
        // Safety coordinator at position 1 (between SkillRankingProvider and L0IdentityProvider)
        if (safety != null)
            providers.Insert(1, safety);
        return providers.ToArray();
    }
}
