using System.ComponentModel;
using LTAI.Agent.Context;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class RetrieveContentTool
{
    private readonly CompressionStore _store;
    private readonly ILogger<RetrieveContentTool> _logger;

    public RetrieveContentTool(CompressionStore store,
        ILogger<RetrieveContentTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RetrieveContentTool>.Instance;
    }

    [Description("Retrieve the original uncompressed content by its compression ID. " +
                  "Use when you see a [CCR: id=\"...\"] marker and need to read the full original content.")]
    [return: Description("The original content, or a not-found message")]
    public string RetrieveContent(
        [Description("The compression ID from a CCR marker (e.g. \"a1b2c3d4e5f6\")")] string id)
    {
        var original = _store.Retrieve(id);
        if (original == null)
        {
            _logger.LogWarning("RetrieveContent: ID '{Id}' not found", id);
            return $"Content with ID '{id}' not found. It may have been cleaned up.";
        }

        _logger.LogDebug("RetrieveContent: ID '{Id}' -> {Len} chars", id, original.Length);
        return original;
    }
}
