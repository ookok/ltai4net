using System.ComponentModel;
using LTAI.AI;
using LTAI.Agent.Memory;

namespace LTAI.Agent.Tools;

[ToolDomain("memory")]
public sealed class ExpandRefTool
{
    private readonly ContextOffloader? _offloader;
    private readonly Pipeline.MessageContext? _context;

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
        var cleanId = refId;
        if (cleanId.StartsWith("[refs/"))
            cleanId = cleanId[6..^1];

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

        return $"Error: ref '{cleanId}' not found";
    }
}
