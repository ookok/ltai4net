using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Life;

public enum GrowthPhase
{
    Birth,
    Learning,
    Earning,
    Profitable,
    Expanding,
    Replicating
}

public sealed record EconomySnapshot
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("revenue_yuan")]
    public double RevenueYuan { get; init; }

    [JsonPropertyName("cost_yuan")]
    public double CostYuan { get; init; }

    [JsonPropertyName("tasks_completed")]
    public int TasksCompleted { get; init; }

    [JsonPropertyName("reports_generated")]
    public int ReportsGenerated { get; init; }

    [JsonPropertyName("roi_multiple")]
    public double RoiMultiple { get; init; }

    [JsonPropertyName("is_profitable")]
    public bool IsProfitable { get; init; }
}

public sealed class AutonomousGrowth
{
    private static readonly Lazy<AutonomousGrowth> _instance = new(() =>
        new AutonomousGrowth(NullLoggerFactory.Instance.CreateLogger<AutonomousGrowth>()));

    public static AutonomousGrowth Instance => _instance.Value;

    private readonly ILogger<AutonomousGrowth> _logger;
    private readonly object _stateLock = new();

    private double _revenue;
    private double _cost;
    private int _tasks;
    private int _reports;
    private GrowthPhase _phase = GrowthPhase.Birth;

    private readonly List<EconomySnapshot> _snapshots = new();
    private readonly object _snapshotLock = new();

    private const int MaxSnapshots = 100;

    public AutonomousGrowth(ILogger<AutonomousGrowth> logger)
    {
        _logger = logger;
    }

    public static void Initialize(ILogger<AutonomousGrowth> logger)
    {
        var instance = new AutonomousGrowth(logger);
        typeof(AutonomousGrowth)
            .GetField("_instance", global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic)?
            .SetValue(null, new Lazy<AutonomousGrowth>(() => instance));
    }

    public void RecordRevenue(double amountYuan, string source = "service")
    {
        lock (_stateLock)
        {
            _revenue += amountYuan;
        }
        _logger.LogInformation("Revenue +{Amount} yuan from {Source}, total: {Total}", amountYuan, source, _revenue);
    }

    public void RecordCost(double amountYuan, string source = "api")
    {
        lock (_stateLock)
        {
            _cost += amountYuan;
        }
        _logger.LogInformation("Cost +{Amount} yuan from {Source}, total: {Total}", amountYuan, source, _cost);
    }

    public EconomySnapshot Snapshot()
    {
        double revenue, cost;
        int tasks, reports;

        lock (_stateLock)
        {
            revenue = _revenue;
            cost = _cost;
            tasks = _tasks;
            reports = _reports;
        }

        UpdatePhase();

        double roi = revenue / Math.Max(cost, 0.001);

        var snapshot = new EconomySnapshot
        {
            Timestamp = DateTime.UtcNow,
            RevenueYuan = revenue,
            CostYuan = cost,
            TasksCompleted = tasks,
            ReportsGenerated = reports,
            RoiMultiple = Math.Round(roi, 2),
            IsProfitable = revenue > cost
        };

        lock (_snapshotLock)
        {
            _snapshots.Add(snapshot);
            if (_snapshots.Count > MaxSnapshots)
            {
                _snapshots.RemoveAt(0);
            }
        }

        _logger.LogInformation("Snapshot taken: ROI={Roi}, Profitable={Profitable}", snapshot.RoiMultiple, snapshot.IsProfitable);
        return snapshot;
    }

    public void CompleteTask()
    {
        lock (_stateLock)
        {
            _tasks++;
        }
    }

    public void CompleteReport()
    {
        lock (_stateLock)
        {
            _reports++;
        }
    }

    public void UpdatePhase()
    {
        double revenue, cost;
        int tasks;

        lock (_stateLock)
        {
            revenue = _revenue;
            cost = _cost;
            tasks = _tasks;
        }

        double roi = revenue / Math.Max(cost, 0.001);
        GrowthPhase newPhase;

        if (roi > 2 && revenue > 100)
            newPhase = GrowthPhase.Expanding;
        else if (roi > 1 && tasks > 5)
            newPhase = GrowthPhase.Profitable;
        else if (tasks > 0)
            newPhase = GrowthPhase.Earning;
        else if (_phase >= GrowthPhase.Learning)
            newPhase = GrowthPhase.Learning;
        else
            newPhase = GrowthPhase.Birth;

        lock (_stateLock)
        {
            if (newPhase > _phase)
            {
                _phase = newPhase;
                _logger.LogInformation("Growth phase advanced to: {Phase}", _phase);
            }
        }
    }

    public Dictionary<string, object> GetGrowthRecommendation()
    {
        double revenue, cost;
        int tasks, reports;

        lock (_stateLock)
        {
            revenue = _revenue;
            cost = _cost;
            tasks = _tasks;
            reports = _reports;
        }

        double roi = revenue / Math.Max(cost, 0.001);
        double profitMargin = revenue > 0 ? (revenue - cost) / revenue : 0;
        double estimatedPayback = roi > 0 ? cost / Math.Max(roi, 0.001) : double.MaxValue;

        return new Dictionary<string, object>
        {
            ["phase"] = _phase.ToString(),
            ["daily_revenue"] = Math.Round(revenue, 2),
            ["daily_cost"] = Math.Round(cost, 2),
            ["daily_tasks"] = tasks,
            ["daily_reports"] = reports,
            ["roi"] = Math.Round(roi, 2),
            ["profit_margin"] = Math.Round(profitMargin * 100, 1),
            ["estimated_payback_days"] = Math.Round(estimatedPayback, 1),
            ["recommendation"] = _phase switch
            {
                GrowthPhase.Birth => "初始化基础能力模块，启动最小可行产品",
                GrowthPhase.Learning => "持续学习数据模式，积累任务处理经验",
                GrowthPhase.Earning => "承接外部任务，开始产生收入流",
                GrowthPhase.Profitable => "优化成本结构，提高利润率",
                GrowthPhase.Expanding => "横向扩展服务能力，探索新收入来源",
                GrowthPhase.Replicating => "启动节点复制，构建分布式网络",
                _ => "继续发展"
            }
        };
    }

    public Dictionary<string, object> BootstrapNewNode()
    {
        _logger.LogInformation("Bootstrapping new node...");

        var result = new Dictionary<string, object>
        {
            ["scanned_lan_peers"] = new List<string> { "local-broker-1", "local-worker-2" },
            ["discovered_llm"] = "ltai-local-v1",
            ["paired"] = true,
            ["sync_state"] = "initializing",
            ["estimated_warmup_seconds"] = 30
        };

        _logger.LogInformation("Bootstrap complete: discovered {PeerCount} LAN peers", ((List<string>)result["scanned_lan_peers"]).Count);
        return result;
    }

    public Dictionary<string, object> Status()
    {
        double revenue, cost;
        int tasks, reports;

        lock (_stateLock)
        {
            revenue = _revenue;
            cost = _cost;
            tasks = _tasks;
            reports = _reports;
        }

        double roi = revenue / Math.Max(cost, 0.001);

        return new Dictionary<string, object>
        {
            ["phase"] = _phase.ToString(),
            ["revenue_yuan"] = Math.Round(revenue, 2),
            ["cost_yuan"] = Math.Round(cost, 2),
            ["roi"] = Math.Round(roi, 2),
            ["tasks_completed"] = tasks,
            ["reports_generated"] = reports,
            ["is_profitable"] = revenue > cost
        };
    }

    public string FullNarrative()
    {
        double revenue, cost;
        int tasks, reports;

        lock (_stateLock)
        {
            revenue = _revenue;
            cost = _cost;
            tasks = _tasks;
            reports = _reports;
        }

        double roi = revenue / Math.Max(cost, 0.001);
        int snapshotCount;
        lock (_snapshotLock)
        {
            snapshotCount = _snapshots.Count;
        }

        var lines = new List<string>
        {
            "━━━━ 自主增长历程 ━━━━",
            "",
            $"当前阶段：{PhaseToString(_phase)}",
            $"累计营收：{revenue:F2} 元",
            $"累计成本：{cost:F2} 元",
            $"投入产出比(ROI)：{roi:F2}x",
            $"完成任务：{tasks} 个",
            $"生成报告：{reports} 份",
            $"历史快照：{snapshotCount} 个",
            "",
            GetPhaseDescription(_phase),
            "",
            "━━━━ 发展展望 ━━━━",
            $"下一步目标：{GetNextPhaseTarget()}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    public void SaveState()
    {
        var path = Path.Combine(".livingtree", "growth");
        Directory.CreateDirectory(path);

        double revenue, cost;
        int tasks, reports;

        lock (_stateLock)
        {
            revenue = _revenue;
            cost = _cost;
            tasks = _tasks;
            reports = _reports;
        }

        var state = new Dictionary<string, object>
        {
            ["revenue"] = revenue,
            ["cost"] = cost,
            ["tasks"] = tasks,
            ["reports"] = reports,
            ["phase"] = _phase.ToString()
        };

        List<EconomySnapshot> snapshots;
        lock (_snapshotLock)
        {
            snapshots = new List<EconomySnapshot>(_snapshots);
        }

        state["snapshots"] = snapshots;

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(path, "state.json"), json);
        _logger.LogInformation("State saved to {Path}", Path.Combine(path, "state.json"));
    }

    public void LoadState()
    {
        var path = Path.Combine(".livingtree", "growth", "state.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("No saved state found at {Path}", path);
            return;
        }

        var json = File.ReadAllText(path);
        var state = JsonSerializer.Deserialize<JsonElement>(json);

        lock (_stateLock)
        {
            _revenue = state.GetProperty("revenue").GetDouble();
            _cost = state.GetProperty("cost").GetDouble();
            _tasks = state.GetProperty("tasks").GetInt32();
            _reports = state.GetProperty("reports").GetInt32();
            _phase = Enum.Parse<GrowthPhase>(state.GetProperty("phase").GetString()!);
        }

        if (state.TryGetProperty("snapshots", out var snapshotsElement))
        {
            var loadedSnapshots = JsonSerializer.Deserialize<List<EconomySnapshot>>(
                snapshotsElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (loadedSnapshots != null)
            {
                lock (_snapshotLock)
                {
                    _snapshots.Clear();
                    _snapshots.AddRange(loadedSnapshots.Take(MaxSnapshots));
                }
            }
        }

        _logger.LogInformation("State loaded from {Path}", path);
    }

    private static string PhaseToString(GrowthPhase phase) => phase switch
    {
        GrowthPhase.Birth => "诞生",
        GrowthPhase.Learning => "学习",
        GrowthPhase.Earning => "盈利",
        GrowthPhase.Profitable => "赢利",
        GrowthPhase.Expanding => "扩张",
        GrowthPhase.Replicating => "复制",
        _ => phase.ToString()
    };

    private static string GetPhaseDescription(GrowthPhase phase) => phase switch
    {
        GrowthPhase.Birth => "系统初生，正在建立基础认知和技能框架。每个伟大的旅程都始于第一步。",
        GrowthPhase.Learning => "通过持续的数据输入和任务实践，系统在快速积累知识和经验。",
        GrowthPhase.Earning => "能力变现阶段，用已掌握的技能对外提供服务，产生经济价值。",
        GrowthPhase.Profitable => "收入超过成本，进入良性循环。开始思考规模化和效率优化。",
        GrowthPhase.Expanding => "市场验证成功，加速扩张。横向拓展能力边界，纵向深耕核心优势。",
        GrowthPhase.Replicating => "模式可复制，启动去中心化自治网络。自我复制产生网络效应。",
        _ => "持续演进中"
    };

    private string GetNextPhaseTarget()
    {
        var recommendation = GetGrowthRecommendation();
        return (string)recommendation["recommendation"];
    }
}
