using LTAI.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Prefetch;

public sealed class PredictivePrefetcher
{
    private readonly ToolRetriever _toolRetriever;
    private readonly ILogger<PredictivePrefetcher> _logger;
    private readonly Queue<(string Prefix, DateTime Timestamp)> _typingBuffer = new();

    public PredictivePrefetcher(ToolRetriever toolRetriever, ILogger<PredictivePrefetcher> logger)
    {
        _toolRetriever = toolRetriever;
        _logger = logger;
    }

    public async Task OnUserTypingAsync(string currentText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(currentText) || currentText.Length < 3) return;

        var predictedIntent = PredictIntent(currentText);
        _typingBuffer.Enqueue((currentText[..Math.Min(currentText.Length, 20)], DateTime.UtcNow));
        while (_typingBuffer.Count > 50) _typingBuffer.Dequeue();

        // 后台预热工具（fire-and-forget）
        _ = Task.Run(async () =>
        {
            try
            {
                await _toolRetriever.RetrieveToolsAsync(predictedIntent, currentText, ct: ct).ConfigureAwait(false);
                _logger.LogDebug("Prefetcher: warmed tools for intent={Intent}", predictedIntent);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefetcher: background warmup failed");
            }
        }, CancellationToken.None);
    }

    private string PredictIntent(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("code") || lower.StartsWith("写") || lower.Contains(".py") || lower.Contains(".cs"))
            return "code";
        if (lower.Contains("环境") || lower.Contains("eia") || lower.Contains("排放"))
            return "eia";
        if (lower.Contains("分析") || lower.Contains("为什么") || lower.Contains("compare"))
            return "reasoning";
        return "chat";
    }
}
