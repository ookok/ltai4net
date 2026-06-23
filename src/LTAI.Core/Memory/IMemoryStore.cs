namespace LTAI.Core.Memory;

public interface IMemoryStore
{
    // L0: Short-term — 当前会话滑动窗口
    Task StoreMessageAsync(string traceId, ChatMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(string traceId, int count = 20, CancellationToken ct = default);

    // L1: Long-term — 持久化结构化记忆
    Task StoreFactAsync(MemoryFact fact, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryFact>> SearchFactsAsync(string query, int topK = 10, MemoryFilter? filter = null, CancellationToken ct = default);

    // L2: Synthesis — 反射/合成记忆
    Task<SynthesizedMemory?> SynthesizeAsync(string topic, CancellationToken ct = default);
    Task<IReadOnlyList<SynthesizedMemory>> GetAllSynthesesAsync(CancellationToken ct = default);

    // GateMem: 记忆治理
    Task ForgetAsync(MemoryForgetRequest request, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);

    // 维护
    Task TrimAsync(int maxEntries = 10000, CancellationToken ct = default);
}

public sealed record MemoryFact(
    string Id,
    string Content,
    string Room,
    float Importance,
    IReadOnlyList<string> Entities,
    float[]? Embedding,
    DateTime CreatedAt,
    string? Principal = null,
    string? Scope = null,
    int? RetentionSeconds = null);

public sealed record SynthesizedMemory(
    string Topic,
    string Summary,
    IReadOnlyList<string> SourceFactIds,
    IReadOnlyList<string> Entities,
    DateTime CreatedAt);

public sealed record MemoryFilter(
    string? Room = null,
    float? MinImportance = null,
    DateTime? Since = null,
    string? EntityName = null,
    string? Principal = null,
    string? Scope = null);

public sealed record MemoryForgetRequest(
    string? FactId = null,
    string? Room = null,
    string? Principal = null,
    string? Scope = null,
    string? EntityName = null,
    bool ForgetAll = false);

public enum MemoryScope
{
    Private,   // 仅创建者可见
    Shared,    // 所有 principal 可见
    Role,      // 特定角色可见（需配合 RoleName）
}

// Minimal ChatMessage type (avoids dependency on MAF)
public sealed record ChatMessage(
    string Role,
    string Content,
    DateTime Timestamp,
    string? TraceId = null);
