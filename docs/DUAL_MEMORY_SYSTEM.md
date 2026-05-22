# 双记忆系统使用指南

> 基于论文 "Useful Memories Become Faulty When Continuously Updated by LLMs" (arXiv:2605.12978) 的启发

## 核心发现

论文揭示了当前 Agent 记忆系统的关键问题：

1. **持续整合会导致记忆退化** - LLM 将原始经验提炼为抽象规则时，效用先升后降
2. **原始片段更可靠** - 仅保留原始轨迹与复杂整合系统表现相当
3. **模型更擅长管理示例** - 保留案例比提炼规则更可靠

## 架构设计

### 双记忆系统

```
┌─────────────────────────────────────────────────────────────┐
│                    双记忆系统 (DualMemoryStore)               │
├──────────────────────────┬──────────────────────────────────┤
│   原始记忆层 (Episodic)   │     抽象记忆层 (Abstract)         │
├──────────────────────────┼──────────────────────────────────┤
│ • 完整交互轨迹            │ • 提炼的规则/教训                 │
│ • 追加不可变              │ • 可更新/删除/冻结                │
│ • 作为主要证据源          │ • 作为辅助参考                    │
│ • 默认保留                │ • 显式门控整合                    │
│ • RawEpisode 记录         │ • AbstractLesson 记录             │
└──────────────────────────┴──────────────────────────────────┘
```

### 门控整合流程

```
新交互 → 存储原始记忆 (RawEpisode)
           ↓
     达到阈值？(默认50条)
           ↓
     冷却时间已过？(默认1小时)
           ↓
     质量监控通过？(预测提升>10%)
           ↓
     执行整合 → 提取抽象教训 (AbstractLesson)
           ↓
     质量检查 → 合格则存储，不合格则拒绝
```

## 快速开始

### 1. 基本使用

```csharp
// DI 已自动注册，直接注入使用
public class MyService
{
    private readonly DualMemoryStore _memory;
    
    public MyService(DualMemoryStore memory)
    {
        _memory = memory;
    }
    
    // 存储原始记忆
    public void StoreInteraction(string query, string response, bool success)
    {
        var episode = new RawEpisode
        {
            Query = query,
            FinalAnswer = response,
            Domain = "code",
            WasSuccessful = success,
            Reward = success ? 0.9f : 0.1f,
            FullTrajectory = $"User: {query}\nAssistant: {response}"
        };
        
        _memory.StoreEpisode(episode);
    }
    
    // 检索相似案例
    public string? FindSimilarCase(string query)
    {
        var episodes = _memory.FindSimilarEpisodes(query, "code");
        if (episodes.Count > 0)
        {
            return episodes[0].FinalAnswer;
        }
        return null;
    }
}
```

### 2. 门控整合

```csharp
// 配置整合策略
var config = new ConsolidationConfig
{
    MinEpisodesToConsolidate = 50,      // 最少50条原始记忆
    QualityThreshold = 0.6f,            // 质量阈值60%
    ImprovementThreshold = 0.1f,        // 需要10%改进
    ConsolidationCooldown = TimeSpan.FromHours(1),  // 1小时冷却
    EnableGatedConsolidation = true     // 启用门控
};

// 执行整合
var result = await _memory.ConsolidateIfNeededAsync(
    async (episodes) =>
    {
        // 使用 LLM 或规则提取器从原始记忆中提取教训
        return await ExtractLessonsFromEpisodes(episodes);
    });

if (result.Success)
{
    Console.WriteLine($"Consolidated: {result.QualifiedLessons} lessons extracted");
}
```

### 3. 记忆质量监控

```csharp
public class QualityCheckService
{
    private readonly MemoryQualityMonitor _monitor;
    
    public async Task CheckMemoryHealthAsync()
    {
        var testQueries = new List<string>
        {
            "How to fix null reference exception?",
            "What is the best practice for async?",
            // ... 更多测试查询
        };
        
        var result = await _monitor.MeasureAsync(testQueries);
        
        Console.WriteLine($"Episodic advantage: {result.EpisodicAdvantage:F2}");
        Console.WriteLine($"Abstract advantage: {result.AbstractAdvantage:F2}");
        Console.WriteLine($"Would consolidation help: {result.WouldConsolidationHelp}");
        
        // 检测记忆退化
        if (_monitor.IsMemoryDegrading())
        {
            Console.WriteLine("WARNING: Memory is degrading!");
        }
        
        // 获取趋势
        var trend = _monitor.GetQualityTrend();
        Console.WriteLine($"Quality trend: {trend:F2} (positive = improving)");
    }
}
```

### 4. 增量 Delta 更新

```csharp
public class RuleManagementService
{
    private readonly IncrementalRuleExtractor _extractor;
    
    public async Task UpdateRulesAsync()
    {
        // 获取未整合的原始记忆
        var newEpisodes = _memoryStore.GetUnconsolidatedEpisodes(100);
        
        // 提取增量规则
        var deltas = await _extractor.ExtractDeltasAsync(newEpisodes);
        
        // 应用增量更新
        var result = await _extractor.ApplyDeltasAsync(deltas);
        
        Console.WriteLine($"Applied: {result.AppliedCount}, Rejected: {result.RejectedCount}");
    }
}
```

## 配置选项

### ConsolidationConfig

| 选项 | 默认值 | 说明 |
|------|--------|------|
| `MinEpisodesToConsolidate` | 50 | 触发整合的最少原始记忆数 |
| `QualityThreshold` | 0.6 | 抽象教训的质量阈值 |
| `ImprovementThreshold` | 0.1 | 需要10%改进才整合 |
| `MaxConsolidationPerCycle` | 10 | 每周期最大整合数 |
| `ConsolidationCooldown` | 1小时 | 整合冷却时间 |
| `EnableGatedConsolidation` | true | 启用门控整合 |

## 最佳实践

### 1. 原始记忆优先

```csharp
// ✅ 推荐：先检索原始记忆
var episodes = _memory.FindSimilarEpisodes(query, domain);
if (episodes.Count > 0 && episodes[0].Confidence > 0.7f)
{
    return episodes[0].FinalAnswer;
}

// 再尝试抽象记忆
var lessons = _memory.FindRelevantLessons(domain);
```

### 2. 显式门控整合

```csharp
// ✅ 推荐：检查是否应该整合
if (_memory.ShouldConsolidate())
{
    var result = await _memory.ConsolidateIfNeededAsync(extractor);
}

// ❌ 避免：每次交互后自动整合
// _memory.ConsolidateAsync();  // 不要这样做
```

### 3. 监控记忆质量

```csharp
// ✅ 推荐：定期检查记忆质量
var trend = _monitor.GetQualityTrend();
if (trend < -0.05f)  // 5%下降
{
    _logger.LogWarning("Memory quality declining!");
}
```

### 4. 增量更新而非全量重写

```csharp
// ✅ 推荐：增量 Delta 更新
var deltas = await _extractor.ExtractDeltasAsync(newEpisodes);
await _extractor.ApplyDeltasAsync(deltas);

// ❌ 避免：全量重写所有规则
// _memory.ClearAllLessons();
// _memory.RebuildAllRules();
```

## 与现有系统集成

### SynapticEvolutionLoop 集成

```csharp
// 在 SynapticEvolutionLoop 中使用双记忆系统
public class EnhancedSynapticEvolutionLoop : BackgroundService
{
    private readonly DualMemoryStore _dualMemory;
    private readonly MemoryQualityMonitor _qualityMonitor;
    
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 检查是否应该整合
            if (_dualMemory.ShouldConsolidate())
            {
                // 质量检查
                var wouldHelp = await _qualityMonitor.WouldConsolidationHelpAsync(testQueries, ct);
                
                if (wouldHelp)
                {
                    await _dualMemory.ConsolidateIfNeededAsync(extractor, ct);
                }
            }
            
            await Task.Delay(TimeSpan.FromMinutes(30), ct);
        }
    }
}
```

## 故障排除

### 整合被拒绝

```
日志：Consolidation not needed (gated or cooldown)
解决：检查 MinEpisodesToConsolidate 和 ConsolidationCooldown 设置
```

### 记忆质量下降

```
日志：Memory quality measured: episodic=0.75 abstract=0.60 dual=0.65
解决：检查抽象教训质量，可能需要冻结低质量规则
```

### 原始记忆过多

```
日志：Unconsolidated episodes: 5000
解决：增加 MaxConsolidationPerCycle 或减少 ConsolidationCooldown
```

## 性能考虑

| 操作 | 时间复杂度 | 说明 |
|------|-----------|------|
| StoreEpisode | O(1) | 追加操作 |
| FindSimilarEpisodes | O(n) | 线性扫描，可优化为向量检索 |
| ConsolidateIfNeededAsync | O(m*k) | m=原始记忆数，k=提取复杂度 |
| GetStats | O(n) | 需要扫描所有记录 |

## 未来优化方向

1. **向量检索** - 使用 Embedding 替代关键词匹配
2. **分布式记忆** - 支持多节点记忆共享
3. **自动阈值调整** - 根据质量趋势动态调整整合参数
4. **记忆压缩** - 长期存储时压缩原始记忆
