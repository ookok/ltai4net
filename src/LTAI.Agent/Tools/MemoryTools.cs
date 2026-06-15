using System.ComponentModel;
using LTAI.AI;
using LTAI.Agent.Memory;
using LTAI.Core.Safety;

namespace LTAI.Agent.Tools;

[ToolDomain("memory")]
public sealed class MemoryTools
{
    private readonly PalaceStore _store;
    private readonly string _defaultWing;

    public MemoryTools(PalaceStore store, string defaultWing = "project")
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _defaultWing = defaultWing;
    }

    [Description("保存一条记忆供未来会话使用。记忆会在下次对话开始时自动加载到上下文中。\n"
        + "适用场景：记住用户偏好设置、保存项目相关的重要决策、记录需要长期保留的信息。\n"
        + "不适用场景：临时会话数据（不需要持久化）、文件内容（请用 WriteFile）。\n"
        + "关键参数：name — 记忆名称(3-40字符)；content — 记忆内容；priority — 优先级；scope — 作用域。")]
    [ToolExample("记住我喜欢的代码风格")]
    [ToolExample("保存这个项目的关键决策")]
    public async Task<string> Remember(
        [Description("Memory name (3-40 chars)")] string name,
        [Description("Content body")] string content,
        [Description("Priority: low, medium, high")] string priority = "medium",
        [Description("Scope: global or project")] string scope = "project")
    {
        if (name.Length < 2 || name.Length > 60)
            return "Name must be 2-60 chars";

        var importance = priority.ToLowerInvariant() switch
        {
            "low" => 0.3,
            "high" => 0.8,
            _ => 0.5,
        };

        // F4: Check content safety before writing to long-term memory
        if (!SafetyRules.IsSafeByRules(content))
            return "⛔ Memory was not saved: content contains sensitive information (API keys, secrets, PII).";

        var wing = scope.ToLowerInvariant() switch
        {
            "global" => "global",
            _ => _defaultWing,
        };

        var drawerId = await _store.StoreAsync(wing, name, content,
            role: "tool",
            importance: importance,
            agentId: "memory_tool",
            ttlMs: PalaceStore.DefaultTtlMs).ConfigureAwait(false);

        return $"✅ Remembered '{name}' ({priority} priority, {scope} scope) — drawer {drawerId[..8]}";
    }

    [Description("删除一条已保存的记忆。\n"
        + "适用场景：清理过时的记忆、删除错误的记录、重置已保存的偏好设置。\n"
        + "关键参数：name — 要删除的记忆名称。")]
    [ToolExample("删除之前保存的那条记忆")]
    public string Forget(
        [Description("Memory name to delete")] string name)
    {
        var drawers = _store.SearchByWingExact(name);
        if (drawers.Count == 0)
            return $"Memory '{name}' not found";

        var count = 0;
        foreach (var d in drawers)
            count += _store.DeleteWingRoom(d.Wing, d.Room);

        return $"🗑️ Forgotten '{name}' ({count} drawer(s) deleted)";
    }

    [Description("读取一条已保存记忆的完整内容。\n"
        + "适用场景：查看之前保存的关键信息、回忆项目决策依据。\n"
        + "关键参数：name — 要读取的记忆名称。")]
    [ToolExample("看看我之前保存了什么")]
    public async Task<string> RecallMemory(
        [Description("Memory name")] string name)
    {
        // Tier 1: Exact wing match (O(1) index lookup)
        var exact = _store.SearchByWingExact(name);
        if (exact.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in exact)
            {
                sb.AppendLine($"--- 📄 {d.Room} (wing: {d.Wing}, importance: {d.Importance:F1}) ---");
                sb.AppendLine(d.Content);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // Tier 2: FTS5 BM25 keyword search (sub-ms, catches content matches)
        var ftsHits = _store.SearchFts(name, topK: 10);
        if (ftsHits.Count > 0)
        {
            var result = new System.Text.StringBuilder();
            result.AppendLine($"## Keyword matches for \"{name}\"");
            foreach (var (drawerId, bm25) in ftsHits.Take(5))
            {
                var drawer = await _store.GetDrawerByIdAsync(drawerId).ConfigureAwait(false);
                if (drawer == null) continue;
                result.AppendLine($"\n--- 📄 {drawer.Room} (wing: {drawer.Wing}, bm25: {bm25:F1}) ---");
                var preview = drawer.Content.Length > 500 ? drawer.Content[..500] + "..." : drawer.Content;
                result.AppendLine(preview);
            }
            return result.ToString();
        }

        // Tier 3: Semantic HNSW search (deep fallback)
        var vec = await _store.GenerateEmbeddingAsync(name).ConfigureAwait(false);
        var hits = new List<(Memory.PalaceStore.Drawer d, double score)>();
        await foreach (var hit in _store.SemanticSearchAsync(vec, topK: 5).ConfigureAwait(false))
            hits.Add(hit);

        if (hits.Count == 0)
            return $"Memory '{name}' not found";

        var semResult = new System.Text.StringBuilder();
        semResult.AppendLine($"## Semantic matches for \"{name}\"");
        foreach (var (d, score) in hits)
        {
            semResult.AppendLine($"\n--- 📄 {d.Room} (wing: {d.Wing}, score: {score:F2}) ---");
            var preview = d.Content.Length > 500 ? d.Content[..500] + "..." : d.Content;
            semResult.AppendLine(preview);
        }
        return semResult.ToString();
    }

    [Description("列出所有已保存的记忆列表。\n"
        + "适用场景：查看有哪些记忆可用、确认记忆名称。\n"
        + "不适用场景：读取单个记忆内容（请用 RecallMemory）。")]
    [ToolExample("我有哪些保存的记忆")]
    public string ListMemories()
    {
        var wings = _store.ListWings();
        if (wings.Count == 0) return "No memories stored yet.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Stored Memories\n");
        foreach (var wing in wings)
        {
            sb.AppendLine($"### Wing: {wing}");
            var drawers = _store.SearchByWing(wing, maxCount: 50);
            foreach (var d in drawers)
            {
                var preview = d.Content.Length > 80 ? d.Content[..80] + "..." : d.Content;
                sb.AppendLine($"- **{d.Room}** (imp: {d.Importance:F1}, {FormatDate(d.CreatedAt)}) — {preview}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatDate(long unixMs)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("MM-dd HH:mm");
        }
        catch
        {
            return "?";
        }
    }
}
