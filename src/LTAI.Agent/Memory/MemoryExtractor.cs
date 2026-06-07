using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Core.Safety;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

/// <summary>
/// Auto-extracts structured facts/preferences from conversation and persists them
/// to <see cref="PalaceStore"/>. Runs after each completed AI turn so the agent
/// doesn't need to explicitly call <c>Remember</c>.
/// </summary>
public sealed partial class MemoryExtractor
{
    private readonly PalaceStore _store;
    private readonly ILogger<MemoryExtractor>? _logger;

    // ── Extraction patterns ──
    //  "我(m)用/喜欢/是/在/有/做/开发/使用 X"
    //  "我的 X 是 Y"
    //  "本项目使用/采用/基于 X"
    //  "I use/like/am/work on X"

    [GeneratedRegex(@"(?:我|I)\s*(?:用|使用|喜欢|prefer|use|like|am|work\s+on)\s*
        (?:的是\s*)?([^，。.!?；;\n]{2,60})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex UserPreferencePattern();

    [GeneratedRegex(@"我的?([^\s，。,!?；;]{1,20})(?:是|为)\s*([^，。.!?；;\n]{2,60})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MyAttributePattern();

    [GeneratedRegex(@"(?:本项目|本工程|这个项目|the\s+project|this\s+project)\s*
        (?:使用|采用|基于|用|is|uses|built|based)\s*
        ([^，。.!?；;\n]{2,60})", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex ProjectFactPattern();

    [GeneratedRegex(@"(?:记住|记得|remember|save|保存)\s*
        (?:[:：])?\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled)]
    private static partial Regex ExplicitSavePattern();

    public MemoryExtractor(PalaceStore store, ILogger<MemoryExtractor>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>Extract facts from the latest user message and store as memories.</summary>
    public async Task ExtractFromTurnAsync(string userMessage, string? entityId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return;

        var extracted = new List<(string wing, string room, string content, double importance)>();

        // 1. Explicit save: "记住: X"
        foreach (Match m in ExplicitSavePattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 3)
                extracted.Add(("user", "saved_note", text, 0.7));
        }

        // 2. User preference: "我用/喜欢 X"
        foreach (Match m in UserPreferencePattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 3)
                extracted.Add(("user", "preference", text, 0.6));
        }

        // 3. "我的 X 是 Y"
        foreach (Match m in MyAttributePattern().Matches(userMessage))
        {
            var attr = m.Groups[1].Value.Trim();
            var val = m.Groups[2].Value.Trim();
            if (attr.Length >= 1 && val.Length >= 1)
                extracted.Add(("user", $"attr_{attr}", $"{attr}: {val}", 0.5));
        }

        // 4. Project facts: "本项目使用 X"
        foreach (Match m in ProjectFactPattern().Matches(userMessage))
        {
            var text = m.Groups[1].Value.Trim();
            if (text.Length >= 3)
                extracted.Add(("project", "tech_stack", text, 0.6));
        }

        if (extracted.Count == 0) return;

        var meta = entityId != null
            ? new Dictionary<string, object> { ["entity_id"] = entityId }
            : null;

        foreach (var (wing, room, content, importance) in extracted)
        {
            if (!SafetyRules.IsSafeByRules(content)) continue;

            try
            {
                await _store.StoreAsync(wing, room, content,
                    role: "user",
                    importance: importance,
                    agentId: "extractor",
                    metadata: meta,
                    ttlMs: PalaceStore.DefaultTtlMs).ConfigureAwait(false);
                _logger?.LogDebug("MemoryExtractor: stored '{Room}' (wing={Wing})", room, wing);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MemoryExtractor: failed to store '{Room}'", room);
            }
        }
    }
}
