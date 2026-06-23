// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DeltaAnchorStep — pipeline step that generates delta-anchored
//  references for tool-executed file edits.
//
//  Injects a summary of the current delta chain for each edited
//  file, allowing subsequent steps (GrammarCheck, QualityGate)
//  to reference errors by delta ID instead of ephemeral line numbers.
//
//  This replaces the fragile "file:line:col" anchor model with
//  content-addressed delta references that survive re-edits.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Delta;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class DeltaAnchorStep : IPipelineStep
{
    public string Name => "DeltaAnchor";
    public int Order => 0; // Runs first in post-generation

    private readonly DeltaStore? _deltaStore;
    private readonly ILogger<DeltaAnchorStep> _logger;

    public DeltaAnchorStep(DeltaStore? deltaStore = null, ILogger<DeltaAnchorStep>? logger = null)
    {
        _deltaStore = deltaStore;
        _logger = logger ?? NullLogger<DeltaAnchorStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (_deltaStore == null) return context;

        var deltaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conversationId = context.TraceId ?? Guid.NewGuid().ToString("n");

        foreach (var (name, args, result) in context.ToolCalls)
        {
            if (!IsFileTool(name)) continue;
            var filePath = ExtractFilePath(args);
            if (filePath == null) continue;

            try
            {
                var parentId = _deltaStore.FindParentDeltaId(filePath);
                var lines = CountLines(result);
                var deltaId = await _deltaStore.CreateDeltaForEditAsync(
                    filePath: filePath,
                    startLine: 1,
                    endLine: Math.Max(1, lines),
                    diffContent: result.Length > 200 ? result[..200] + "..." : result,
                    toolName: name,
                    conversationId: conversationId,
                    messageId: $"msg-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    agentId: "DeltaAnchorStep").ConfigureAwait(false);

                deltaMap[filePath] = deltaId;
                _logger.LogDebug("DeltaAnchorStep: anchored {File} → delta:{DeltaId}",
                    filePath, deltaId[..12]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeltaAnchorStep: failed to anchor {File}", filePath);
            }
        }

        if (deltaMap.Count > 0)
        {
            context.Set("DeltaAnchors", deltaMap);
        }

        return context;
    }

    private static bool IsFileTool(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower is "writefile" or "editfile" or "applypatches" or "filewritetool";
    }

    private static string? ExtractFilePath(string args)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(args);
            if (doc.RootElement.TryGetProperty("path", out var p))
                return p.GetString();
        }
        catch { }
        return null;
    }

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var count = 1;
        foreach (var c in s)
            if (c == '\n') count++;
        return count;
    }
}
