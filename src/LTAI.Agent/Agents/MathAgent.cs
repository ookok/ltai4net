using System;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MathNet.Numerics;

namespace LTAI.Agent.Agents;

public sealed class MathAgent : BaseAgent
{
    public MathAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<MathAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct)
    {
        var q = context.UserQuery;
        if (q.Contains("factorial", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(q, @"\d+");
            if (m.Success && int.TryParse(m.Value, out var n) && n >= 0 && n <= 20)
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, $"{n}! = {SpecialFunctions.Factorial(n)}"));
        }
        return await CallBrainAsync(context.FullHistory, ct: ct);
    }
}

