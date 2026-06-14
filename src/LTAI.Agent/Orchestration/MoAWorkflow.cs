using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Orchestration;

public sealed class MoAWorkflow
{
    private readonly IReadOnlyList<IChatClient> _proposers;
    private readonly IReadOnlyList<IChatClient> _aggregators;
    private readonly ILogger<MoAWorkflow> _logger;
    private static readonly int _moaConcurrency = int.TryParse(
        Environment.GetEnvironmentVariable("LTAI_MOA_CONCURRENCY"), out var c) ? Math.Max(1, c) : 6;
    private static readonly SemaphoreSlim _throttle = new(_moaConcurrency, _moaConcurrency);
    private readonly TimeSpan _workflowTimeout;

    public MoAWorkflow(
        IReadOnlyList<IChatClient> proposers,
        IReadOnlyList<IChatClient> aggregators,
        ILogger<MoAWorkflow> logger,
        TimeSpan? workflowTimeout = null)
    {
        _proposers = proposers;
        _aggregators = aggregators;
        _logger = logger;
        _workflowTimeout = workflowTimeout ?? TimeSpan.FromSeconds(120);
    }

    public int ProposerCount => _proposers.Count;
    public int LayerCount => _aggregators.Count;

    public async Task<string> ExecuteAsync(string query, CancellationToken ct = default)
    {
        if (_proposers.Count == 0 || _aggregators.Count == 0)
        {
            _logger.LogWarning("MoA: empty proposers or aggregators, returning empty");
            return "";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_workflowTimeout);
        var effectiveCt = timeoutCts.Token;

        _logger.LogInformation("MoA: {P} proposers, {L} layers starting", ProposerCount, LayerCount);

        // Layer 0: K proposers generate candidates in parallel (throttled)
        var proposerTasks = _proposers.Select(async (client, i) =>
        {
            await _throttle.WaitAsync(effectiveCt).ConfigureAwait(false);
            try { return await CallProposerAsync(client, i, query, effectiveCt).ConfigureAwait(false); }
            finally { _throttle.Release(); }
        });
        var proposals = await Task.WhenAll(proposerTasks).ConfigureAwait(false);

        _logger.LogInformation("MoA: Layer 0 complete, {C} proposals", proposals.Length);

        // Layer 1..L: aggregators progressively synthesize
        var currentInputs = proposals.ToList();
        for (int layer = 0; layer < LayerCount; layer++)
        {
            var aggregator = _aggregators[layer];
            var aggregationTasks = currentInputs.Select(async (input, i) =>
            {
                await _throttle.WaitAsync(effectiveCt).ConfigureAwait(false);
                try { return await CallAggregatorAsync(aggregator, layer, i, query, currentInputs, effectiveCt).ConfigureAwait(false); }
                finally { _throttle.Release(); }
            });
            var outputs = await Task.WhenAll(aggregationTasks).ConfigureAwait(false);
            currentInputs = outputs.ToList();
            _logger.LogInformation("MoA: Layer {L} complete, {C} outputs", layer + 1, outputs.Length);
        }

        // Final layer: single aggregator produces the result
        var finalInput = currentInputs.Count == 1
            ? currentInputs[0]
            : await CallFinalAggregatorAsync(_aggregators[^1], query, currentInputs, ct).ConfigureAwait(false);

        _logger.LogInformation("MoA: complete, final output length={Len}", finalInput?.Length ?? 0);
        return finalInput ?? "";
    }

    private async Task<string> CallProposerAsync(IChatClient client, int index, string query, CancellationToken ct)
    {
        var prompt = $"""
            你是第 {index + 1} 号提议者。请独立思考以下问题，不要参考其他人的意见。
            输出你的完整方案：

            {query}
            """;

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], null, ct).ConfigureAwait(false);
        return response.Messages?.LastOrDefault()?.Text ?? "";
    }

    private async Task<string> CallAggregatorAsync(IChatClient client, int layer, int index,
        string query, List<string> inputs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"你是第 {index + 1} 号聚合者（第 {layer + 1} 层）。");
        sb.AppendLine($"请综合以下 {inputs.Count} 个方案，生成一个更完善的版本：");
        sb.AppendLine();
        for (int i = 0; i < inputs.Count; i++)
        {
            sb.AppendLine($"--- 方案 {i + 1} ---");
            sb.AppendLine(inputs[i]);
            sb.AppendLine();
        }
        sb.AppendLine("请输出你的综合结果：");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, sb.ToString())], null, ct).ConfigureAwait(false);
        return response.Messages?.LastOrDefault()?.Text ?? "";
    }

    private async Task<string> CallFinalAggregatorAsync(IChatClient client,
        string query, List<string> inputs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是最终聚合者。请综合以下所有方案，输出最终答案：");
        sb.AppendLine($"原始问题：{query}");
        sb.AppendLine();
        for (int i = 0; i < inputs.Count; i++)
        {
            sb.AppendLine($"--- 方案 {i + 1} ---");
            sb.AppendLine(inputs[i]);
            sb.AppendLine();
        }
        sb.AppendLine("请输出最终答案：");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, sb.ToString())], null, ct).ConfigureAwait(false);
        return response.Messages?.LastOrDefault()?.Text ?? "";
    }
}
