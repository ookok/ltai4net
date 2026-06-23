using LTAI.AI;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#pragma warning disable MAAI001

namespace LTAI.Agent;

/// <summary>
/// Assembles the 7-layer memory palace + tool RAG + safety coordinator into the
/// ordered <see cref="AIContextProvider"/> array that <c>HarnessAgentOptions</c> consumes.
///
/// Layer order matters — the harness walks providers in this order on every turn.
///
/// Note: ToolRetrievalProvider (removed) was replaced by ToolFilteringChatClient
/// (a MAF IChatClient middleware) to avoid ordering conflicts with
/// HarnessAgent's built-in providers (FileAccessProvider, BackgroundAgentsProvider).
/// Tool filtering now runs at the IChatClient level, after all AIContextProviders
/// have merged their tools.
/// </summary>
internal sealed class AgentContextProviderBuilder
{
    private readonly IOptions<LTAIOptions> _opts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SkillEvolutionEngine _skillEngine;
    private readonly CompressionStore _compressionStore;
    private readonly Indexing.ProvenanceProvider _provenance;
    private readonly LspLanguageManager _lsp;
    private readonly Glove50Embedder? _glove;
    private readonly EntropyTracker? _entropy;
    private readonly EditLedger _editLedger;

    public AgentContextProviderBuilder(
        IOptions<LTAIOptions> opts,
        ILoggerFactory loggerFactory,
        SkillEvolutionEngine skillEngine,
        CompressionStore compressionStore,
        Indexing.ProvenanceProvider provenance,
        LspLanguageManager lsp,
        EditLedger editLedger,
        Glove50Embedder? glove = null,
        EntropyTracker? entropy = null)
    {
        _opts = opts;
        _loggerFactory = loggerFactory;
        _skillEngine = skillEngine;
        _compressionStore = compressionStore;
        _provenance = provenance;
        _lsp = lsp;
        _editLedger = editLedger;
        _glove = glove;
        _entropy = entropy;
    }

    public AIContextProvider[] Build(string name, string identityText,
        CompactionProvider compaction, KbGraph kbGraph, CgGraph codeGraph,
        Indexing.CodeChunkIndex codeChunkIndex, WasmtimeSandbox wasmtimeSandbox,
        EmbeddingClient embedder, PalaceStore palaceStore,
        string identityText2, string? modelId,
        AgentSkillsProvider skillsProvider,
        SafetyCoordinator? safety)
    {
        var specSvc = new SpecService(_opts.Value.ResolveDataPath("specs"));

        var providers = new List<AIContextProvider>(20)
        {
            new SkillRankingProvider(_skillEngine,
                _loggerFactory.CreateLogger<SkillRankingProvider>()),
            new MemoryAuthorityProvider(),
            new LookaheadProviderSelector(embedder,
                _loggerFactory.CreateLogger<LookaheadProviderSelector>()),
            new L0IdentityProvider(identityText),
            new L1EssentialProvider(palaceStore, name, _entropy,
                _loggerFactory.CreateLogger<L1EssentialProvider>()),
            new SpecContextProvider(specSvc,
                _loggerFactory.CreateLogger<SpecContextProvider>()),
            compaction,
            new CCRProvider(_compressionStore,
                _loggerFactory.CreateLogger<CCRProvider>()),
            kbGraph, codeGraph, codeChunkIndex, wasmtimeSandbox,
            new L3OnDemandProvider(palaceStore, _entropy,
                _loggerFactory.CreateLogger<L3OnDemandProvider>()),
            new L4DeepSearchProvider(palaceStore, embedder, _entropy,
                _loggerFactory.CreateLogger<L4DeepSearchProvider>()),
            new L6AgentDiaryProvider(palaceStore, name,
                _loggerFactory.CreateLogger<L6AgentDiaryProvider>()),
            _provenance,
            new InstructionProvider(modelId),
            new EnvironmentProvider(), skillsProvider,
            new CacheAlignerProvider(
                _loggerFactory.CreateLogger<CacheAlignerProvider>()),
            new LspDiagnosticsProvider(_lsp),
            new EditLedgerProvider(_editLedger, maxTokens: 200),
        };

        // Safety coordinator at position 1 (between SkillRankingProvider and MemoryAuthorityProvider)
        if (safety != null)
            providers.Insert(1, safety);

        // Warm up LookaheadProviderSelector domain centroids (background, non-blocking)
        if (embedder != null)
            _ = LookaheadProviderSelector.WarmupCentroidsAsync(embedder, _glove);

        return providers.ToArray();
    }
}
