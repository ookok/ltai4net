using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Governors;

public sealed record PolicyTrainingResult
{
    public string PolicyId { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public int PromptsBefore { get; init; }
    public int PromptsAfter { get; init; }
    public int PromptsPruned { get; init; }
    public double AvgRewardBefore { get; init; }
    public double AvgRewardAfter { get; init; }
    public bool Improved { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public interface IPromptPolicy
{
    string PolicyId { get; }
    string PolicyName { get; }
    Task<PolicyTrainingResult> TrainAsync(IReadOnlyList<PromptExperience> experiences, CancellationToken ct = default);
    void RecordTrial(string query, string response, double reward);
    double GetSuccessRate();
}

public sealed class MultiPolicyTrainer
{
    private readonly ConcurrentDictionary<string, IPromptPolicy> _policies = new();
    private readonly SharedReplayBuffer _replayBuffer;
    private readonly ILogger<MultiPolicyTrainer> _logger;
    private readonly Channel<PromptExperience> _experienceChannel = Channel.CreateUnbounded<PromptExperience>();
    private readonly int _batchSize;
    private readonly TimeSpan _trainInterval;
    private readonly CancellationTokenSource _trainCts = new();
    private Task? _backgroundTrainLoop;

    public MultiPolicyTrainer(
        SharedReplayBuffer replayBuffer,
        int batchSize = 64,
        TimeSpan? trainInterval = null,
        ILogger<MultiPolicyTrainer>? logger = null)
    {
        _replayBuffer = replayBuffer;
        _batchSize = batchSize;
        _trainInterval = trainInterval ?? TimeSpan.FromSeconds(30);
        _logger = logger ?? NullLogger<MultiPolicyTrainer>.Instance;
    }

    public MultiPolicyTrainer RegisterPolicy(IPromptPolicy policy)
    {
        _policies[policy.PolicyId] = policy;
        _logger.LogInformation("Registered policy: {Id} ({Name})", policy.PolicyId, policy.PolicyName);
        return this;
    }

    public MultiPolicyTrainer UnregisterPolicy(string policyId)
    {
        _policies.TryRemove(policyId, out _);
        _logger.LogInformation("Unregistered policy: {Id}", policyId);
        return this;
    }

    public IReadOnlyList<string> PolicyIds => _policies.Keys.ToList();

    public async Task StartAsync(CancellationToken ct = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _trainCts.Token);
        _backgroundTrainLoop = BackgroundTrainLoopAsync(linkedCts.Token);
        _logger.LogInformation("MultiPolicyTrainer started with {Count} policies, batch={Batch}, interval={Interval}s",
            _policies.Count, _batchSize, _trainInterval.TotalSeconds);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _trainCts.Cancel();
        if (_backgroundTrainLoop != null)
        {
            try { await _backgroundTrainLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _logger.LogInformation("MultiPolicyTrainer stopped");
    }

    public void PushExperience(PromptExperience experience)
    {
        _replayBuffer.Push(experience);
        _experienceChannel.Writer.TryWrite(experience);
    }

    public async Task PushExperiencesAsync(IReadOnlyList<PromptExperience> experiences, CancellationToken ct = default)
    {
        foreach (var exp in experiences)
        {
            _replayBuffer.Push(exp);
            await _experienceChannel.Writer.WriteAsync(exp, ct).ConfigureAwait(false);
        }
    }

    public async Task<PolicyTrainingResult?> TrainPolicyAsync(string policyId, CancellationToken ct = default)
    {
        if (!_policies.TryGetValue(policyId, out var policy))
        {
            _logger.LogWarning("Policy {Id} not found", policyId);
            return null;
        }

        var experiences = _replayBuffer.Sample(_batchSize, policyId: policyId);
        if (experiences.Count == 0)
        {
            _logger.LogDebug("No experiences for policy {Id}, skipping training", policyId);
            return null;
        }

        var prevRate = policy.GetSuccessRate();
        var result = await policy.TrainAsync(experiences, ct).ConfigureAwait(false);

        _logger.LogInformation("Policy {Id} trained: reward {Prev:F3}→{After:F3}, improved={Improved}",
            policyId, prevRate, result.AvgRewardAfter, result.Improved);

        return result;
    }

    public async Task<List<PolicyTrainingResult>> TrainAllAsync(CancellationToken ct = default)
    {
        var results = new List<PolicyTrainingResult>();

        foreach (var policyId in _policies.Keys)
        {
            var result = await TrainPolicyAsync(policyId, ct).ConfigureAwait(false);
            if (result != null) results.Add(result);
        }

        return results;
    }

    public async Task ShareBestPromptsAsync(CancellationToken ct = default)
    {
        var policyStats = _replayBuffer.GetPolicyStats();
        if (policyStats.Count < 2) return;

        var bestPolicyId = policyStats.OrderByDescending(kv => kv.Value).First().Key;
        var bestExperiences = _replayBuffer.Sample(_batchSize / 2, policyId: bestPolicyId);

        foreach (var (policyId, policy) in _policies)
        {
            if (policyId == bestPolicyId) continue;

            var crossLabeled = bestExperiences
                .Select(e => e with { PolicyId = policyId })
                .ToList();

            foreach (var exp in crossLabeled)
                _replayBuffer.Push(exp);
        }

        _logger.LogInformation("Shared {Count} best experiences from {BestPolicy} to {OtherCount} policies",
            bestExperiences.Count, bestPolicyId, _policies.Count - 1);
    }

    public IReadOnlyDictionary<string, double> GetAllPolicyStats()
        => _replayBuffer.GetPolicyStats();

    private async Task BackgroundTrainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_trainInterval, ct).ConfigureAwait(false);

                await TrainAllAsync(ct).ConfigureAwait(false);

                if (_replayBuffer.Count > _batchSize * 2)
                    await ShareBestPromptsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Training loop error (will retry)");
            }
        }
    }
}

public sealed class SimplePromptPolicy : IPromptPolicy
{
    private readonly List<string> _prompts;
    private readonly ConcurrentDictionary<string, (double Reward, int Count)> _promptStats = new();
    private double _totalReward;
    private int _totalTrials;
    private readonly object _mutateLock = new();
    private int _mutateCounter;

    public string PolicyId { get; }
    public string PolicyName { get; }
    public string Domain { get; set; } = "general";

    public SimplePromptPolicy(string policyId, string policyName, IEnumerable<string>? seedPrompts = null)
    {
        PolicyId = policyId;
        PolicyName = policyName;
        _prompts = seedPrompts?.ToList() ?? new List<string> { "You are a helpful assistant." };
    }

    public void RecordTrial(string query, string response, double reward)
    {
        var promptShort = query[..Math.Min(query.Length, 80)];
        _promptStats.AddOrUpdate(promptShort,
            _ => (reward, 1),
            (_, v) => (v.Reward + reward, v.Count + 1));

        _totalReward += reward;
        Interlocked.Increment(ref _totalTrials);
    }

    public double GetSuccessRate()
        => _totalTrials > 0 ? _totalReward / _totalTrials : 0;

    public Task<PolicyTrainingResult> TrainAsync(IReadOnlyList<PromptExperience> experiences, CancellationToken ct = default)
    {
        var beforeCount = _prompts.Count;
        var beforeReward = GetSuccessRate();

        foreach (var exp in experiences)
            RecordTrial(exp.Query, exp.Response, exp.Reward);

        var pruneThreshold = GetSuccessRate() * 0.5;
        var toRemove = _promptStats
            .Where(kv => kv.Value.Count >= 5 && kv.Value.Reward / kv.Value.Count < pruneThreshold)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            _promptStats.TryRemove(key, out _);

        Interlocked.Increment(ref _mutateCounter);
        if (_mutateCounter % 10 == 0 && experiences.Count >= 3)
        {
            var topExp = experiences.OrderByDescending(e => e.Reward).Take(3).ToList();
            lock (_mutateLock)
            {
                foreach (var exp in topExp)
                {
                    if (_prompts.Count < 20 && !_prompts.Contains(exp.Query))
                        _prompts.Add(exp.Query[..Math.Min(exp.Query.Length, 200)]);
                }
            }
        }

        var afterReward = GetSuccessRate();

        return Task.FromResult(new PolicyTrainingResult
        {
            PolicyId = PolicyId,
            PolicyName = PolicyName,
            PromptsBefore = beforeCount,
            PromptsAfter = _promptStats.Count,
            PromptsPruned = toRemove.Count,
            AvgRewardBefore = beforeReward,
            AvgRewardAfter = afterReward,
            Improved = afterReward > beforeReward,
        });
    }

    public IReadOnlyList<string> GetPrompts() => _prompts.AsReadOnly();
}
