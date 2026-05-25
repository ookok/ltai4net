using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.DNA.Consciousness;
using LTAI.DNA.Safety;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class ChatAgent : BaseAgent
{
    private readonly List<(string role, string content)> _conversationHistory = new();
    private readonly PersonaDriftDetector? _driftDetector;
    private const int MaxHistoryTurns = 20;

    public ChatAgent(
        LTAIAgentCard card,
        IChatClient brain,
        SkillRegistry skills,
        ILogger<ChatAgent> logger,
        Personality? personality = null)
        : base(card, brain, skills, logger)
    {
        if (personality != null)
            _driftDetector = new PersonaDriftDetector(personality);
    }

    protected override async Task<AgentResponse> ExecuteLogicAsync(
        AgentContext context, CancellationToken ct)
    {
        var query = context.UserQuery;
        _logger.LogInformation("ChatAgent [{Name}]: {Query}", Name, query[..Math.Min(query.Length, 200)]);

        UpdateHistory("user", query);

        if (query.Trim().StartsWith('/'))
            return await HandleSlashCommand(query, ct).ConfigureAwait(false);

        var msgList = new List<ChatMessage>(context.FullHistory);

        if (_conversationHistory.Count > 6)
        {
            var systemMsg = new ChatMessage(ChatRole.System,
                "Previous conversation summary:\n" + string.Join("\n",
                    _conversationHistory.TakeLast(10).Select(h =>
                        $"[{h.role}]: {h.content[..Math.Min(h.content.Length, 200)]}")));
            msgList.Insert(0, systemMsg);
        }

        var response = await CallBrainAsync(msgList, ct: ct).ConfigureAwait(false);
        var responseText = response.Text ?? "";
        UpdateHistory("assistant", responseText[..Math.Min(responseText.Length, 500)]);

        _driftDetector?.RecordInteraction(query, responseText);

        if (_driftDetector?.ShouldTriggerPersonaRefresh() == true)
        {
            var alert = _driftDetector.Analyze();
            _logger.LogWarning("ChatAgent [{Name}]: Persona drift detected (score={Score:F2}, severity={Severity})",
                Name, alert?.DriftScore ?? 0, alert?.Severity.ToString() ?? "Unknown");

            var reinforcementPrompt = _driftDetector.GetPersonaReinforcementPrompt();
            var reinforcedMessages = new List<ChatMessage>(msgList)
            {
                new(ChatRole.System, reinforcementPrompt)
            };

            response = await CallBrainAsync(reinforcedMessages, ct: ct).ConfigureAwait(false);
            responseText = response.Text ?? "";
            UpdateHistory("assistant", responseText[..Math.Min(responseText.Length, 500)]);
        }

        return response;
    }

    private async Task<AgentResponse> HandleSlashCommand(string command, CancellationToken ct)
    {
        var cmd = command.Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "/help":
                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                    "LTAI Chat Agent. Commands: /help /status /clear /budget"));
            case "/status":
                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                    $"Chat Agent [{Name}] — History: {_conversationHistory.Count} turns"));
            case "/clear":
                _conversationHistory.Clear();
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "Conversation history cleared."));
            case "/budget":
                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                    "Budget tracking is enabled for this agent."));
            default:
                return await CallBrainAsync(
                    new List<ChatMessage> { new(ChatRole.User, command) }, ct: ct).ConfigureAwait(false);
        }
    }

    private void UpdateHistory(string role, string content)
    {
        _conversationHistory.Add((role, content));
        while (_conversationHistory.Count > MaxHistoryTurns)
            _conversationHistory.RemoveAt(0);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CallBrainStreamingAsync(messages, cancellationToken))
            yield return update;
    }
}
