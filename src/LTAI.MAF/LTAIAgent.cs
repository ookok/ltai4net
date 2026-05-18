using LTAI.AI.Governors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.MAF;

public sealed class LTAIAgent
{
    private readonly LivingTreeSystem _livingTree;
    private readonly ILogger<LTAIAgent> _logger;
    private readonly LTAIInputFilter? _inputFilter;
    private readonly LTAIOutputFilter? _outputFilter;

    public string Name => "LTAI";
    public string Description => "LivingTree AI Agent with bio-inspired governance";

    public LTAIAgent(
        LivingTreeSystem livingTree,
        ILogger<LTAIAgent> logger,
        LTAIInputFilter? inputFilter = null,
        LTAIOutputFilter? outputFilter = null)
    {
        _livingTree = livingTree;
        _logger = logger;
        _inputFilter = inputFilter;
        _outputFilter = outputFilter;
    }

    public async Task<ChatMessage> ProcessAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();

        if (userMessages.Count == 0)
            return new ChatMessage(ChatRole.Assistant, "No user message received.");

        var query = string.Join("\n", userMessages.Select(m => m.Text ?? ""));

        if (_inputFilter != null)
        {
            var lastMsg = userMessages.Last();
            var (enriched, label, complexity, emotion) = _inputFilter.Analyze(lastMsg);
        }

        _logger.LogInformation("LTAI agent: {Query}", query[..Math.Min(query.Length, 200)]);

        try
        {
            var result = await _livingTree.ChatAsync(query, cancellationToken);

            if (_outputFilter != null)
                result = _outputFilter.Review(result);

            return new ChatMessage(ChatRole.Assistant, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LTAI agent error");
            return new ChatMessage(ChatRole.Assistant, $"Error: {ex.Message}");
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var response = await ProcessAsync(messages, cancellationToken);
        return new ChatResponse(response);
    }

    public async Task<string> ChatAsync(string query, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var result = await ProcessAsync(messages, cancellationToken);
        return result.Text ?? "";
    }
}
