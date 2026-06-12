# Batch 2: 编排多样性增强 + MoA 聚合

**来源论文**:
- [Diversity Collapse in Multi-Agent LLM Systems](https://arxiv.org/abs/2604.18005), ACL 2026 Findings
- [Multi-Agent Reasoning Improves Compute Efficiency: Pareto-Optimal Test-Time Scaling](https://arxiv.org/abs/2605.01566), ACL 2026

**优先级**: P1 | **工作量**: 10-15 人天 | **目标模块**: `src/LTAI.Agent/AgentWorkflows.cs`, `src/LTAI.Agent/Orchestration/`

## 现状分析与痛点

1. **多样性无保障**: Concurrent 模式 fan-out 到多个 agent 后简单 fan-in，无多样性保护。Sequential 模式前一个 agent 的输出直接喂入后一个，产生"锚定效应"。
2. **聚合策略单一**: 多个 agent 输出只是文本拼接，无结构化聚合层。
3. **Self-refinement 可能有害**: LTAI-Review 走 review-critique-revise 流程，类似 self-refinement，根据 MoA Pareto 论文，缺少外部反馈时反而降分。
4. **预算分配盲目**: 所有任务走同样的编排路径，无难度自适应。

## 论文核心发现

### Diversity Collapse 核心结论

- 强模型 + 权威驱动角色 + 密集通信拓扑 = 语义多样性崩溃
- NGT (Nominal Group Technique: 先盲写再讨论) 在初期保持最高多样性
- 子组拓扑 (Subgroup: 3 组 × 2 agent) 后期维持最高建设性冲突密度
- 权威抑制多样性: 初级研究者主导的协作比跨学科专家组多样性高 73% (Vendi 8.08 vs 4.65)

### MoA Pareto 核心结论

- MoA (Mixture-of-Agents) 是 Pareto 前沿上最高效的测试时扩展方法
- Proposer 数 = Layer 数 + 1 (如 5 models / 4 layers)
- Debate 优先加 agent 数而非 round 数 (round 过多放大错误)
- Self-refinement 在无外部反馈时低于 CoT baseline
- Easy 题收益仅 +2.2pp, Hard 题收益 +9.0pp → 按难度分配预算

## 改进目标

1. 在 Concurrent workflow 中新增 NGT 和 Subgroup 编排模式
2. 新增 MoA 聚合层替代简单文本拼接
3. Review 流程从 self-refinement 改为并行 review + consensus
4. DecisionTreeRouter 增加任务难度估计，动态选择编排策略

## 详细设计

### 1. NGT 模式 (Blind-First Concurrent)

```csharp
// AgentWorkflows.cs 新增
public enum ConcurrentMode
{
    Standard,    // 现有: 并行 → 直接聚合
    NGT,         // 新增: 并行盲写 → 汇总共享 → 第二轮讨论 → 聚合
    Subgroup     // 新增: 子组并行 → 组内共识 → 组间聚合
}

// NGT 执行流程
// Phase 1 (Blind): K 个 agent 独立处理，看不到他人输出
// Phase 2 (Share): 汇总所有 Phase 1 输出，分享给所有 agent
// Phase 3 (Discuss): agent 基于他人输出进行第二轮推理
// Phase 4 (Aggregate): 结构化聚合 Phase 3 输出
```

### 2. MoA 聚合层

```csharp
// 新增 MoAWorkflow.cs
public class MoAWorkflow
{
    int ProposerCount { get; set; }  // K
    int LayerCount { get; set; }     // L = K - 1

    // Layer 0: K 个 proposer agent 并行生成候选方案
    // Layer 1..L: aggregator 逐步综合 (每层输入前层所有输出)
    // 最终层: 单 agent 输出最终结果
}
```

**配置规则**: `ProposerCount = LayerCount + 1` (经验最优)

**Model 分配**: proposer 使用 L2/L3 模型, aggregator 可使用 L1 模型 (proposer 质量决定上限, aggregator 只做综合)

### 3. Review 流程重构

```
当前: agent → review → critique → revise (self-refinement, 无外部信号)
改进: agent 生成 → K 个 reviewer 并行评审 (NGT blind) → 共享评审意见
      → 第二轮针对性修改 → MoA 聚合评审 → 最终输出
```

### 4. 难度自适应路由

```csharp
// DecisionTreeRouter.cs 增强
public enum OrchestrationStrategy
{
    Direct,          // 简单: 单 agent CoT
    SelfConsistency, // 中等: 并行采样投票
    NGT,             // 中等+: 先盲写再讨论
    MoA              // 困难: K proposer + L aggregator
}

// 难度估计: 基于 query 的 embedding 距离 + 工具调用复杂度 + 历史成功率
```

### 5. 多样性监控

```csharp
// 新增 DiversityMonitor.cs
public class DiversityMonitor
{
    // 用 Vendi Score (核矩阵谱熵) 实时监测输出多样性
    // 低于阈值 → 自动触发 NGT 或 Subgroup 重组
    double Threshold { get; set; } = 0.5;
    void Monitor(IReadOnlyList<string> agentOutputs);
    OrchestrationStrategy RecommendStrategy(double currentVendiScore);
}
```

## 涉及文件清单

| 操作 | 文件 |
|------|------|
| 修改 | `src/LTAI.Agent/AgentWorkflows.cs` — NGT/Subgroup/MoA 模式 |
| 新增 | `src/LTAI.Agent/Orchestration/MoAWorkflow.cs` |
| 新增 | `src/LTAI.Agent/Orchestration/DiversityMonitor.cs` |
| 修改 | `src/LTAI.Agent/Orchestration/DecisionTreeRouter.cs` — 难度估计 + 策略选择 |
| 修改 | `agents/LTAI-Review.agent.md` — review 流程调整为并行+consensus |
| 修改 | `.livingtree/workflows/sequential.json` — 新增 NGT/Subgroup/MoA 配置模板 |
| 修改 | `.livingtree/workflows/concurrent.json` — 新增 mode 参数 |
| 修改 | `.livingtree/workflows/decision-tree.json` — 新增难度阈值 |

## 验收标准

1. [ ] NGT 模式下输出多样性 (Vendi Score) 相比 Standard 提升 25%+
2. [ ] MoA 聚合 (3 proposer / 2 layer) 在复杂代码生成任务上 Pass@1 提升 3-5%
3. [ ] 难度路由: Hard 题走 MoA, Easy 题走 Direct, 整体准确率无退化
4. [ ] Review 流程重构后评审质量不低于当前水平
5. [ ] 3 个新编排模式各 10 个集成测试通过

## 参考

- Diversity Collapse: https://arxiv.org/abs/2604.18005
- MoA Pareto: https://arxiv.org/abs/2605.01566
- MoA Pareto code: https://github.com/Multi-Agent-LLMs/lm-evaluation-harness
