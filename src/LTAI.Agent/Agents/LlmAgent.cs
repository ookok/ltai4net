using System;
using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
using OllamaSharp;

namespace LTAI.Agent.Agents;

public sealed class LlmAgent : BaseAgent
{
    public LlmAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<LlmAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct)
    {
        var q = context.UserQuery;

        if (q.Contains("ollama", OrdinalIgnoreCase) && (q.Contains("list", OrdinalIgnoreCase) || q.Contains("model", OrdinalIgnoreCase)))
            return await OllamaListAsync(ct);
        if (q.Contains("ollama", OrdinalIgnoreCase) && q.Contains("chat", OrdinalIgnoreCase))
            return await OllamaChatAsync(q, ct);

        return await CallBrainAsync(context.FullHistory, ct: ct);
    }

    private async Task<AgentResponse> OllamaListAsync(CancellationToken ct)
    {
        try
        {
            var client = new OllamaClient();
            var models = await client.ListLocalModelsAsync(ct);
            var sb = new System.Text.StringBuilder("Ollama Models:\n");
            foreach (var m in models)
                sb.AppendLine($"  {m.Name} ({m.Digest[..12]})");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
        }
        catch (Exception ex)
        {
            return Fail($"Ollama: {ex.Message}");
        }
    }

    private async Task<AgentResponse> OllamaChatAsync(string query, CancellationToken ct)
    {
        try
        {
            var model = query.Split(' ').SkipWhile(w => !w.Contains(':')).FirstOrDefault() ?? "llama3.2";
            var client = new OllamaClient { SelectedModel = model };
            var response = await client.CompleteAsync(query, ct);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, response?.Response ?? "No response"));
        }
        catch (Exception ex)
        {
            return Fail($"Ollama: {ex.Message}");
        }
    }
}


