using System.ComponentModel;
using LTAI.AI;
using LTAI.Agent.Memory;

namespace LTAI.Agent.Tools;

[ToolDomain("memory")]
public sealed class ExpandRefTool
{
    private readonly ContextOffloader? _offloader;
    private readonly Pipeline.MessageContext? _context;

    // KVEraser-inspired: max chars for lightweight expansion (summary mode)
    private const int LightweightMaxChars = 800;
    private const int LightweightMaxLines = 15;

    public ExpandRefTool(ContextOffloader? offloader = null, Pipeline.MessageContext? context = null)
    {
        _offloader = offloader;
        _context = context;
    }

    [Description("展开一个 [refs/...] 引用，查看完整内容。如果 refs 内容已被压缩掉，可通过此工具恢复原始数据。")]
    [return: Description("refs 引用的完整原始内容")]
    public async Task<string?> ExpandRef(
        [Description("refs ID，如 filename.md#hash 或完整 [refs/filename.md#hash] 格式")] string refId)
    {
        return await ExpandRefCore(refId, lightweight: false).ConfigureAwait(false);
    }

    /// <summary>
    /// KVEraser-inspired lightweight refs expansion: returns a compact summary
    /// instead of full text. Reduces context injection cost while preserving
    /// key information. Use when full content is not needed.
    /// </summary>
    [Description("轻量级展开 refs 引用 — 只返回摘要（前800字符+前15行），不注入全文。适用于只需确认内容概要的场景。")]
    [return: Description("refs 引用的摘要内容")]
    public async Task<string?> ExpandRefLightweight(
        [Description("refs ID，如 filename.md#hash 格式")] string refId)
    {
        return await ExpandRefCore(refId, lightweight: true).ConfigureAwait(false);
    }

    private async Task<string?> ExpandRefCore(string refId, bool lightweight)
    {
        var cleanId = refId;
        if (cleanId.StartsWith("[refs/"))
            cleanId = cleanId[6..^1];

        var fullContent = await ResolveContentAsync(cleanId).ConfigureAwait(false);
        if (fullContent == null)
            return $"Error: ref '{cleanId}' not found";

        if (!lightweight || fullContent.Length <= LightweightMaxChars)
            return fullContent;

        // KVEraser: lightweight expansion — summary only
        var lines = fullContent.Split('\n');
        var summaryLines = lines.Take(LightweightMaxLines).ToList();

        var summary = string.Join("\n", summaryLines);
        if (summary.Length > LightweightMaxChars)
            summary = summary[..LightweightMaxChars];

        var totalLines = lines.Length;
        var totalChars = fullContent.Length;

        return $"{summary}\n\n---\n[轻量级展开: 显示前 {LightweightMaxLines}/{totalLines} 行, " +
               $"{LightweightMaxChars}/{totalChars} 字符. 使用 ExpandRef 查看完整内容.]";
    }

    private async Task<string?> ResolveContentAsync(string cleanId)
    {
        // Try lazy restore from MessageContext first
        if (_context != null)
        {
            var restored = await _context.RestoreRefAsync(cleanId).ConfigureAwait(false);
            if (restored != null)
            {
                _context.RecordRefExpansion(cleanId);
                return restored;
            }
        }

        // Fall back to direct file read
        if (_offloader != null)
        {
            var content = await _offloader.ReadRefAsync(cleanId).ConfigureAwait(false);
            if (content != null)
            {
                _context?.RecordRefExpansion(cleanId);
                return content;
            }
        }

        // Try reading from .livingtree/refs/ directly
        var parts = cleanId.Split('#');
        var filePath = Path.Combine(
            _offloader?.RefsDirectory ?? Path.Combine(AppContext.BaseDirectory, ".livingtree", "refs"),
            parts[0]);
        if (File.Exists(filePath))
        {
            _context?.RecordRefExpansion(cleanId);
            return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        }

        return null;
    }
}
