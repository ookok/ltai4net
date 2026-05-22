# Fast-Slow Learning 架构使用指南

> 基于论文 "Learning, Fast and Slow: Towards LLMs That Adapt Continually" (arXiv:2605.12484)

## 核心概念

Fast-Slow Training (FST) 将 LLM 适应分为两个互补的组件：

| 组件 | Fast Weights | Slow Weights |
|------|--------------|--------------|
| **载体** | 优化的上下文/提示词 | 模型参数 |
| **更新频率** | 频繁（每次交互后） | 稀疏（每 N 次交互后） |
| **更新成本** | 低 | 高 |
| **目标** | 任务特定适应 | 通用推理能力 |
| **优化器** | GEPA 反射优化 | RL/梯度下降 |

## 架构设计

```
┌─────────────────────────────────────────────────────────────┐
│              FastSlowCellAI 协同进化引擎                     │
├──────────────────────────┬──────────────────────────────────┤
│   Fast Loop (快速循环)    │     Slow Loop (慢速循环)          │
├──────────────────────────┼──────────────────────────────────┤
│ • 动态提示词上下文        │ • 预训练 ONNX 模型                │
│ • GEPA 反射优化          │ • ML.NET 自训练分类器             │
│ • 原始记忆检索            │ • 抽象教训存储                    │
│ • 每 10 次交互更新        │ • 每 50 次交互更新                │
├──────────────────────────┼──────────────────────────────────┤
│        ↓ 协同决策 ↓       │                                  │
│   加权组合 (Fast 0.6 + Slow 0.4)                              │
└──────────────────────────┴──────────────────────────────────┘
```

## 快速开始

### 1. 基本使用

```csharp
// DI 已自动注册，直接注入使用
public class MyService
{
    private readonly FastSlowCellAI _fastSlowAI;
    
    public MyService(FastSlowCellAI fastSlowAI)
    {
        _fastSlowAI = fastSlowAI;
    }
    
    public async Task<string> ProcessQueryAsync(string query)
    {
        var result = await _fastSlowAI.ProcessAsync(query);
        
        if (result.Activated)
        {
            Console.WriteLine($"Source: {result.Source}");
            Console.WriteLine($"Confidence: {result.Confidence:F2}");
            Console.WriteLine($"Latency: {result.LatencyMs:F1}ms");
            
            return result.Response;
        }
        
        // 回退到 LLM
        return await _llm.GetResponseAsync(query);
    }
}
```

### 2. 配置 Fast-Slow 策略

```csharp
var config = new FastSlowConfig
{
    FastUpdateInterval = 10,           // 每 10 次交互更新快速上下文
    SlowUpdateInterval = 50,           // 每 50 次交互更新慢速模型
    FastWeight = 0.6f,                 // 快速结果权重 60%
    SlowWeight = 0.4f,                 // 慢速结果权重 40%
    MaxFastContexts = 20,              // 最大快速上下文数
    MinSamplesForSlowTraining = 30,    // 慢速训练最小样本数
    EnableCoEvolution = true           // 启用协同进化
};

services.AddSingleton<FastSlowCellAI>(sp =>
{
    var cellRegistry = sp.GetRequiredService<CellAIRegistry>();
    var memoryStore = sp.GetRequiredService<DualMemoryStore>();
    var promptOptimizer = sp.GetRequiredService<GEPAPromptOptimizer>();
    return new FastSlowCellAI(cellRegistry, memoryStore, promptOptimizer, config);
});
```

### 3. 使用 GEPA 优化提示词

```csharp
public class PromptOptimizationService
{
    private readonly GEPAPromptOptimizer _optimizer;
    
    public async Task<List<PromptCandidate>> OptimizeDomainAsync(
        string domain,
        List<InteractionResult> interactions)
    {
        // 获取现有上下文
        var existingContexts = _optimizer.GetParetoFrontier(domain);
        
        // 执行优化
        var optimized = await _optimizer.OptimizeAsync(
            domain,
            interactions,
            existingContexts);
        
        // 查看 Pareto 前沿
        var stats = _optimizer.GetStats();
        Console.WriteLine($"Total optimizations: {stats.TotalOptimizations}");
        Console.WriteLine($"Total reflections: {stats.TotalReflections}");
        
        return optimized;
    }
}
```

## 核心组件

### FastSlowCellAI

快慢协同进化引擎，负责：

1. **Fast Loop**: 使用动态上下文快速适应
2. **Slow Loop**: 使用模型进行深度推理
3. **协同决策**: 加权组合快慢结果
4. **自动更新**: 根据交互频率更新快慢组件

### GEPAPromptOptimizer

GEPA 风格的提示词优化器，实现：

1. **ASI 提取**: 从交互中提取可操作的侧面信息
2. **自然语言反思**: 基于反馈诊断问题
3. **候选生成**: 变异、新生成、合并候选
4. **Pareto 前沿**: 维护准确率 vs 多样性的最优候选集

### ActionableSideInfo (ASI)

可操作的侧面信息，包含：

- `ErrorMessages`: 错误消息
- `ReasoningLogs`: 推理日志
- `ToolCallTraces`: 工具调用追踪
- `SuccessPatterns`: 成功模式
- `FailurePatterns`: 失败模式
- `ScalarReward`: 标量奖励
- `Diagnosis`: 诊断结果
- `Suggestion`: 改进建议

## 工作流程

### 单次查询处理

```
用户查询
  ↓
Fast Loop: 使用动态上下文检索相似案例
  ↓
Slow Loop: 使用模型进行深度推理
  ↓
协同决策: 加权组合 (Fast 0.6 + Slow 0.4)
  ↓
返回结果 + 记录交互
  ↓
检查是否需要更新
  ├─ Fast Update (每 10 次)
  └─ Slow Update (每 50 次)
```

### GEPA 优化循环

```
交互收集
  ↓
提取 ASI (可操作侧面信息)
  ↓
自然语言反思
  ↓
生成候选 (变异/新生成/合并)
  ↓
评估候选
  ↓
更新 Pareto 前沿
```

## 监控和统计

### FastSlowCellAI 统计

```csharp
var stats = _fastSlowAI.GetStats();

Console.WriteLine($"Total interactions: {stats.TotalInteractions}");
Console.WriteLine($"Fast contexts: {stats.FastContextCount}");
Console.WriteLine($"Slow models: {stats.SlowModelCount}");
Console.WriteLine($"Fast updates: {stats.FastUpdates}");
Console.WriteLine($"Slow updates: {stats.SlowUpdates}");
```

### GEPA 统计

```csharp
var stats = _optimizer.GetStats();

Console.WriteLine($"Total optimizations: {stats.TotalOptimizations}");
Console.WriteLine($"Total reflections: {stats.TotalReflections}");
Console.WriteLine($"Domains: {stats.DomainCount}");
```

## 最佳实践

### 1. 调整更新频率

```csharp
// 快速变化的领域：更频繁的 Fast 更新
var config = new FastSlowConfig
{
    FastUpdateInterval = 5,   // 每 5 次交互
    SlowUpdateInterval = 25   // 每 25 次交互
};

// 稳定领域：更稀疏的更新
var config = new FastSlowConfig
{
    FastUpdateInterval = 20,  // 每 20 次交互
    SlowUpdateInterval = 100  // 每 100 次交互
};
```

### 2. 调整快慢权重

```csharp
// 偏向快速适应（新领域）
var config = new FastSlowConfig
{
    FastWeight = 0.8f,
    SlowWeight = 0.2f
};

// 偏向慢速推理（成熟领域）
var config = new FastSlowConfig
{
    FastWeight = 0.3f,
    SlowWeight = 0.7f
};
```

### 3. 启用协同进化

```csharp
var config = new FastSlowConfig
{
    EnableCoEvolution = true  // 快速上下文指导慢速训练
};
```

## 与双记忆系统集成

Fast-Slow 架构与双记忆系统天然互补：

| Fast-Slow 组件 | 双记忆系统组件 | 协同方式 |
|---------------|---------------|---------|
| Fast Context | Episodic Store | 从原始记忆检索案例 |
| Slow Model | Abstract Store | 使用抽象教训训练 |
| GEPA Optimizer | MemoryQualityMonitor | 质量反馈指导优化 |
| Co-Evolution | IncrementalRuleExtractor | 增量更新规则 |

```csharp
// FastSlowCellAI 自动与双记忆系统集成
// 无需额外配置
```

## 性能考虑

| 操作 | 时间复杂度 | 说明 |
|------|-----------|------|
| ProcessAsync | O(fast + slow) | 并行执行快慢循环 |
| UpdateFastContexts | O(n*k) | n=交互数，k=GEPA 复杂度 |
| UpdateSlowModels | O(m*t) | m=样本数，t=训练时间 |
| CoEvolveAsync | O(c*v) | c=上下文数，v=验证数 |

## 故障排除

### Fast 上下文质量下降

```
日志：Low quality context removed: id=xxx quality=0.35
解决：检查 GEPA 优化器是否正确提取 ASI
```

### Slow 模型训练失败

```
日志：Insufficient samples for slow training: 20/30
解决：增加交互收集或降低 MinSamplesForSlowTraining
```

### Pareto 前沿退化

```
日志：GEPA optimization completed: candidates=0
解决：检查交互质量，确保有足够的成功/失败模式
```

## 未来优化方向

1. **并行快慢循环** - 使用 Task.WhenAll 并行执行
2. **自适应权重** - 根据领域自动调整 Fast/Slow 权重
3. **分布式 Pareto 前沿** - 支持多节点共享前沿
4. **增量 GEPA** - 仅优化受影响的候选而非全量
