using LTAI.AI.Governors;
using LTAI.AI.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed class LogiInputFilter
{
    private readonly ILogger<LogiInputFilter> _logger;

    public LogiInputFilter(ILogger<LogiInputFilter> logger)
    {
        _logger = logger;
    }

    public (ChatMessage enriched, string label, float complexity, string emotion) Analyze(
        ChatMessage message)
    {
        var query = message.Text ?? "";
        var (complexity, label) = GovernorUtilities.ClassifyIntent(query);
        var emotion = GovernorUtilities.DetectEmotion(query);

        _logger.LogInformation("MAF input: label={Label}, complexity={Complexity:F2}, emotion={Emotion}",
            label, complexity, emotion);

        var enrichedText = query;
        if (!string.IsNullOrEmpty(emotion) && emotion != "neutral")
            enrichedText = $"[emotion: {emotion}] {query}";

        return (new ChatMessage(ChatRole.User, enrichedText), label, complexity, emotion);
    }
}

public sealed class LogiOutputFilter
{
    private readonly ILogger<LogiOutputFilter> _logger;

    public LogiOutputFilter(ILogger<LogiOutputFilter> logger)
    {
        _logger = logger;
    }

    public string Review(string responseText)
    {
        if (string.IsNullOrEmpty(responseText))
            return responseText;

        var (isHallucinated, reason) = GovernorUtilities.CheckHallucination(responseText);
        if (isHallucinated)
        {
            _logger.LogWarning("MAF output: hallucination risk - {Reason}", reason);
        }

        return responseText;
    }
}
