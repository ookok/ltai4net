using LTAI.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 事件驱动计划治理管道 ====================

public record PlanGovernorConfig
{
    public bool EnableAutoInvalidation { get; init; } = true;
    public int HighImpactPriorityThreshold { get; init; } = 5;  // 高优先级事件阈值
    public TimeSpan ReplanningCooldown { get; init; } = TimeSpan.FromMinutes(5);  // 重规划冷却
    public int MaxReplansPerHour { get; init; } = 10;  // 每小时最大重规划次数
}

public sealed class FastSlowGovernorPipeline : IDisposable
{
    private readonly IEventBusV2 _eventBus;
    private readonly CellAnswerStore _planStore;
    private readonly FastSlowCellAI _fastSlowAI;
    private readonly DualMemoryStore _memoryStore;
    private readonly ILogger<FastSlowGovernorPipeline> _logger;
    private readonly PlanGovernorConfig _config;
    private readonly Dictionary<string, DateTime> _lastReplanTimes = new();
    private readonly object _lock = new();
    private int _totalInvalidations;
    private int _totalReplans;

    public FastSlowGovernorPipeline(
        IEventBusV2 eventBus,
        CellAnswerStore planStore,
        FastSlowCellAI fastSlowAI,
        DualMemoryStore memoryStore,
        PlanGovernorConfig? config = null,
        ILogger<FastSlowGovernorPipeline>? logger = null)
    {
        _eventBus = eventBus;
        _planStore = planStore;
        _fastSlowAI = fastSlowAI;
        _memoryStore = memoryStore;
        _config = config ?? new PlanGovernorConfig();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FastSlowGovernorPipeline>.Instance;

        if (_config.EnableAutoInvalidation)
        {
            SubscribeToEvents();
        }

        _logger.LogInformation(
            "FastSlowGovernorPipeline initialized: autoInvalidation={Auto} threshold={Threshold}",
            _config.EnableAutoInvalidation,
            _config.HighImpactPriorityThreshold);
    }

    /// <summary>
    /// 订阅高优先级事件，触发计划失效和重规划
    /// </summary>
    private void SubscribeToEvents()
    {
        // 订阅高优先级系统事件
        _eventBus.Subscribe("system.high_impact", OnHighImpactEvent);

        // 订阅用户中断事件
        _eventBus.Subscribe("user.interrupt", OnUserInterrupt);

        // 订阅错误事件
        _eventBus.Subscribe("error.critical", OnCriticalError);

        // 订阅领域变更事件
        _eventBus.Subscribe("domain.change", OnDomainChange);

        _logger.LogInformation("Event subscriptions registered for plan governance");
    }

    /// <summary>
    /// 处理高优先级事件
    /// </summary>
    private void OnHighImpactEvent(LivingEvent evt)
    {
        if (!_config.EnableAutoInvalidation) return;

        var cellId = evt.Data.TryGetValue("cell_id", out var cellIdObj) ? cellIdObj.ToString() ?? "" : "";
        var priority = evt.Priority;

        if (string.IsNullOrEmpty(cellId))
        {
            _logger.LogWarning("High impact event missing cell_id");
            return;
        }

        if (priority >= _config.HighImpactPriorityThreshold)
        {
            _logger.LogInformation(
                "High impact event detected: cell={Cell} priority={Priority} type={Type}",
                cellId, priority, evt.EventType);

            InvalidateAndReplan(cellId, $"high_impact_event:{evt.EventType}", priority);
        }
    }

    /// <summary>
    /// 处理用户中断
    /// </summary>
    private void OnUserInterrupt(LivingEvent evt)
    {
        if (!_config.EnableAutoInvalidation) return;

        var cellId = evt.Data.TryGetValue("cell_id", out var cellIdObj) ? cellIdObj.ToString() ?? "" : "";
        if (string.IsNullOrEmpty(cellId)) return;

        _logger.LogInformation("User interrupt detected for cell: {Cell}", cellId);
        InvalidateAndReplan(cellId, "user_interrupt", evt.Priority);
    }

    /// <summary>
    /// 处理关键错误
    /// </summary>
    private void OnCriticalError(LivingEvent evt)
    {
        if (!_config.EnableAutoInvalidation) return;

        var cellId = evt.Data.TryGetValue("cell_id", out var cellIdObj) ? cellIdObj.ToString() ?? "" : "";
        if (string.IsNullOrEmpty(cellId)) return;

        _logger.LogWarning("Critical error detected for cell: {Cell}", cellId);
        InvalidateAndReplan(cellId, $"critical_error:{evt.Data.GetValueOrDefault("error_type", "unknown")}", 10);
    }

    /// <summary>
    /// 处理领域变更
    /// </summary>
    private void OnDomainChange(LivingEvent evt)
    {
        if (!_config.EnableAutoInvalidation) return;

        var cellId = evt.Data.TryGetValue("cell_id", out var cellIdObj) ? cellIdObj.ToString() ?? "" : "";
        var newDomain = evt.Data.TryGetValue("new_domain", out var domainObj) ? domainObj.ToString() ?? "" : "";
        if (string.IsNullOrEmpty(cellId)) return;

        _logger.LogInformation("Domain change detected: cell={Cell} newDomain={Domain}", cellId, newDomain);
        InvalidateAndReplan(cellId, $"domain_change:{newDomain}", 3);
    }

    /// <summary>
    /// 执行计划失效和重规划
    /// </summary>
    private void InvalidateAndReplan(string cellId, string reason, int priority)
    {
        lock (_lock)
        {
            // 检查重规划冷却
            if (_lastReplanTimes.TryGetValue(cellId, out var lastReplan))
            {
                var timeSinceLastReplan = DateTime.UtcNow - lastReplan;
                if (timeSinceLastReplan < _config.ReplanningCooldown)
                {
                    _logger.LogDebug(
                        "Replan cooldown active: cell={Cell} remaining={Remaining:F0}s",
                        cellId, (_config.ReplanningCooldown - timeSinceLastReplan).TotalSeconds);
                    return;
                }
            }

            // 检查每小时重规划次数限制
            var recentReplans = _lastReplanTimes.Values
                .Count(t => DateTime.UtcNow - t < TimeSpan.FromHours(1));
            if (recentReplans >= _config.MaxReplansPerHour)
            {
                _logger.LogWarning(
                    "Replan limit reached for cell: {Cell} count={Count}/{Max}",
                    cellId, recentReplans, _config.MaxReplansPerHour);
                return;
            }
        }

        // 执行计划失效
        var invalidationEvent = _planStore.InvalidatePlans(cellId, reason, priority);
        _totalInvalidations++;

        _logger.LogInformation(
            "Plans invalidated: cell={Cell} affected={Count} reason={Reason}",
            cellId, invalidationEvent.AffectedPlanIds.Count, reason);

        // 触发重规划 (异步)
        Task.Run(async () =>
        {
            try
            {
                await ReplanForCellAsync(cellId, invalidationEvent).ConfigureAwait(false);

                lock (_lock)
                {
                    _lastReplanTimes[cellId] = DateTime.UtcNow;
                    _totalReplans++;
                }

                _logger.LogInformation(
                    "Replan completed: cell={Cell} totalReplans={Total}",
                    cellId, _totalReplans);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Replan failed for cell: {Cell}", cellId);
            }
        });
    }

    /// <summary>
    /// 为指定 Cell 执行重规划
    /// </summary>
    private async Task ReplanForCellAsync(string cellId, PlanInvalidationEvent invalidationEvent)
    {
        // 1. 从双记忆系统检索相关经验
        var relevantEpisodes = _memoryStore.FindSimilarEpisodes(
            $"replan reason: {invalidationEvent.Reason}",
            limit: 5);

        // 2. 使用 FastSlowCellAI 生成新计划
        var replanQuery = BuildReplanQuery(cellId, invalidationEvent, relevantEpisodes);
        var replanResult = await _fastSlowAI.ProcessAsync(replanQuery).ConfigureAwait(false);

        // 3. 创建新的分层计划
        if (replanResult.Activated)
        {
            _planStore.CreatePlan(
                cellId,
                PlanLevel.Immediate,
                replanResult.Response,
                replanResult.Domain,
                validity: TimeSpan.FromMinutes(15));

            _logger.LogInformation(
                "New immediate plan created: cell={Cell} domain={Domain}",
                cellId, replanResult.Domain);
        }
    }

    /// <summary>
    /// 构建重规划查询
    /// </summary>
    private static string BuildReplanQuery(
        string cellId,
        PlanInvalidationEvent invalidationEvent,
        List<RawEpisode> relevantEpisodes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Cell {cellId} needs replanning due to: {invalidationEvent.Reason}");
        sb.AppendLine();

        if (relevantEpisodes.Count > 0)
        {
            sb.AppendLine("## Relevant Past Experiences:");
            foreach (var episode in relevantEpisodes.Take(3))
            {
                sb.AppendLine($"- Query: {episode.Query}");
                sb.AppendLine($"  Answer: {episode.FinalAnswer[..Math.Min(50, episode.FinalAnswer.Length)]}");
                sb.AppendLine($"  Reward: {episode.Reward:F2}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Generate a new immediate action plan based on this context.");

        return sb.ToString();
    }

    /// <summary>
    /// 获取治理统计
    /// </summary>
    public GovernorStats GetStats()
    {
        lock (_lock)
        {
            return new GovernorStats
            {
                TotalInvalidations = _totalInvalidations,
                TotalReplans = _totalReplans,
                ActiveReplanCooldowns = _lastReplanTimes.Count(
                    kvp => DateTime.UtcNow - kvp.Value < _config.ReplanningCooldown)
            };
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("FastSlowGovernorPipeline disposed");
    }
}

public record GovernorStats
{
    public int TotalInvalidations { get; init; }
    public int TotalReplans { get; init; }
    public int ActiveReplanCooldowns { get; init; }
}
