using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class CapabilityGovernor : LayerGovernor
{
    private readonly AIToolRegistry _tools;

    public CapabilityGovernor(ICognitiveMesh mesh, IChatClient llm, ILogger<CapabilityGovernor> logger, AIToolRegistry tools)
        : base("capability", mesh, llm, logger)
    {
        _tools = tools;
    }

    public override async Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var toolName = incoming.Payload?.GetValueOrDefault("tool")?.ToString();
        var parameters = incoming.Payload?.GetValueOrDefault("params") as Dictionary<string, object?>;

        if (toolName == null)
        {
            return new Handshake
            {
                From = LayerName,
                Action = "tools_list",
                Payload = new Dictionary<string, object?> { ["tools"] = _tools.ListTools().ToList() }
            };
        }

        try
        {
            var result = await _tools.InvokeAsync(toolName, parameters ?? new(), cancellationToken);
            return new Handshake
            {
                From = LayerName,
                Action = "tool_result",
                Payload = new Dictionary<string, object?> { ["result"] = result, ["tool"] = toolName }
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Tool invocation failed: {Tool}", toolName);
            return ErrorResponse($"Tool '{toolName}' failed: {ex.Message}");
        }
    }
}
