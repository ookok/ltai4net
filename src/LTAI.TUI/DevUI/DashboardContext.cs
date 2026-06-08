using LTAI.Agent.Context;
using LTAI.Agent.DevUI;
using LTAI.Agent.Memory;
using LTAI.Agent.Tasks;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;
using LTAI.AI;

namespace LTAI.TUI.DevUI;

public sealed record DashboardContext(
    LTAIDevUIService DevUi,
    DevUISpanCollector SpanCollector,
    YAMLWorkflowRegistry? Workflows,
    LocalEmbedder? Embedder,
    ToolEmbeddingCache? EmbedCache,
    RemoteEmbeddingCache? RemoteCache,
    EmbeddingClient? EmbeddingClient,
    ModelMetadataProvider? ModelsProvider,
    CacheAlignerProvider? Aligner,
    TaskQueue? TaskQueue,
    BackgroundJobService? Bgjs,
    PalaceStore? Palace = null)
{
    public WorkflowHealthTracker? WorkflowHealth { get; init; }
}
