using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class GenerationOrderStep : IPipelineStep
{
    private readonly ReachIndex? _reachIndex;
    private readonly CgGraph? _cgGraph;
    private readonly ILogger<GenerationOrderStep> _logger;

    public string Name => "GenerationOrder";

    public GenerationOrderStep(
        CgGraph? cgGraph = null,
        ReachIndex? reachIndex = null,
        ILogger<GenerationOrderStep>? logger = null)
    {
        _cgGraph = cgGraph;
        _reachIndex = reachIndex ?? cgGraph?.ReachIndex;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GenerationOrderStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (_reachIndex == null || !_reachIndex.Built)
        {
            _logger.LogDebug("GenerationOrderStep: ReachIndex not available, skipping");
            return context;
        }

        try
        {
            var symbols = ExtractSymbolsFromRequest(context.Request);
            if (symbols.Count == 0)
            {
                _logger.LogDebug("GenerationOrderStep: no symbols found in request");
                return context;
            }

            var order = new List<string>();
            var visited = new HashSet<string>();

            foreach (var symbol in symbols)
            {
                await TopologicalOrderAsync(symbol, order, visited, context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (order.Count > 0)
            {
                var plan = "## Generation Order\n";
                for (int i = 0; i < order.Count; i++)
                    plan += $"{i + 1}. {order[i]}\n";

                lock (context.MessagesLock)
                    context.Messages.Add(new ChatMessage(ChatRole.System, plan));

                context.Set("GenerationOrder", order);
                _logger.LogInformation("GenerationOrderStep: generated {Count} step order", order.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GenerationOrderStep failed");
        }

        return context;
    }

    private async Task TopologicalOrderAsync(string symbol, List<string> order, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(symbol)) return;

        if (_cgGraph != null)
        {
            try
            {
                var symIds = await _cgGraph.ResolveSymbolIdsAsync(symbol, limit: 3).ConfigureAwait(false);
                foreach (var symId in symIds)
                {
                    var impact = _reachIndex!.QueryImpact(symId, depth: 3);
                    foreach (var depId in impact.ReverseReachable)
                    {
                        var depName = await ResolveNameAsync(depId).ConfigureAwait(false) ?? $"symbol_{depId}";
                        if (!order.Contains(depName))
                            await TopologicalOrderAsync(depName, order, visited, ct).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
            }
        }

        if (!order.Contains(symbol))
            order.Add(symbol);
    }

    private async Task<string?> ResolveNameAsync(long id)
    {
        if (_cgGraph == null) return null;
        try
        {
            var node = await _cgGraph.GetNodeAsync(id).ConfigureAwait(false);
            return node?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractSymbolsFromRequest(string request)
    {
        var symbols = new List<string>();
        var parts = request.Split([' ', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.Contains('.') || trimmed.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                if (trimmed.Length >= 2 && char.IsUpper(trimmed[0]))
                    symbols.Add(trimmed);
            }
        }
        return symbols.Distinct().ToList();
    }
}
