# LTAI V0.6 硬化审计报告

> 审计官：LTAI V0.6 Hardened Auditor
> 审计日期：2026-05-27
> 默认立场：极度不信任

---

## 修复有效性评估 (Fix Validation)

| 组件 | 状态 | 硬化评语 |
|------|------|-----------|
| BootstrapTeacher | ⚠️ 部分激活 | `AdvancePhaseIfReadyAsync` 有肉了，但事件发布机制是断的，阈值仍是硬编码 |
| ArchitectLoop | ⚠️ 部分激活 | Rollback/Adjust 有了实质逻辑，但完全是内存级操作且无破坏性行为防护 |
| GenePool | ❌ 装活 | Crossover/Mutate 仍是字符串拼接操作，Gene 数据结构无结构化意图层 |
| SimulatedAnnealer | ⚠️ 部分激活 | 接入了真实评估，但 embedding 是伪哈希，评估权重仍是魔法数字 |

---

## 🔪 硬化刺穿点 (Where it breaks)

### 1. 事件总线断路——假进化闭环

**位置**: `BootstrapTeacher.cs:275`

```csharp
internal Action<CoordinationEvent>? CoordinationPublisher { get; set; }
```

整个代码库中，没有任何代码设置过这个属性。`BootstrapTeacher.Publish()` 调用 `CoordinationPublisher?.Invoke(evt)`——因为永远是 `null`，所有阶段推进事件直接射入虚空。`CoordinationScheduler.RegisterBootstrapRules()` 注册了监听 `BootstrapPhaseAdvanced` 事件的规则（触发 GenePool.Evolve + SimulatedAnnealer.StepAsync），但事件从未送达。

**判断**: 这依然是僵尸连接。如果 `AdvancePhaseIfReadyAsync` 只是自己改了 `_phase` 字段但没有通知生态系统的其他部分，那所谓的"阶段推进"就只是自我安慰。

---

### 2. 残留的硬编码毒瘤——阈值无法进化

**位置**: `BootstrapTeacher.cs:40-45`

```csharp
private const int TeachingQuota = 2000;
private const double TeachingAccuracyThreshold = 0.85;
private const int ShadowingExtraQueries = 1000;
private const double ShadowingAccuracyThreshold = 0.95;
private const double ShadowRate = 0.10;
private const double AutonomousSpotCheckRate = 0.02;
```

`const` 在 C# 中是编译时常量，直接内联到 IL 中。GenePool 无法触碰这些值——即使它生成了 100 代基因提议"把 TeachingQuota 降到 500"，也绝无可能改变运行时行为。`ArchitectureAction` 枚举里没有 `AdjustTeachingQuota` 或 `AdjustAccuracyThreshold` 选项。

**判断**: 这是人类拍脑袋定的数字，系统的"进化"永远绕不过这些墙。

---

### 3. Gene 仍是字符串玩具而非结构化意图

**位置**: `GenePool.cs:7-21`

```csharp
public sealed record Gene
{
    public string Condition { get; init; } = "";
    public string Action { get; init; } = "";
    ...
}
```

- **Mutate** (`GenePool.cs:265-293`): 只能切换 `&&` ↔ `||`、`>` ↔ `<` 等操作符——永远无法改变 Condition 的语义内容，只能重组已有的 token。
- **Crossover** (`GenePool.cs:130-161`): token 级字符串剪切拼接，无语义理解。

**判断**: 这依然是盲目变异。Gene 无法产生如 `TargetModule = ParetoRouter, Operation = ChangeShadowRate, Value = 0.15` 的结构化意图。

---

### 4. 进化断层——Gene 无法触发规则文件的物理写入闭环

- `GeneToRule.DeployTopGenesAsync` (`SimulatedAnnealer.cs:267`) → 部署到 `ParetoRouter` ✅
- `GeneToRule.SyncKeywordsToClassifier` (`SimulatedAnnealer.cs:350`) → 修改 `L0IntentClassifier` 内存规则 ✅
- `L0IntentClassifier.PersistRulesAsync` (`L0IntentClassifier.cs:139`) → 写入 `rules/*.md` ✅
- 但 **`DeployTopGenesAsync` 从不调用 `PersistRulesAsync`** ❌

实际调用 `PersistRulesAsync` 的唯一途径是 `ArchitectureAction.PersistRules`——需要 L2 LLM 明确选择该 Action。没有任何方法在部署 Gene 后自动完成 "内存 → 文件 → 重启加载" 的全闭环。

**判断**: 整个链路上每一步都是独立的。系统重启后只能加载旧规则，除非 L2 恰好在某个偶然时刻选择了 PersistRules。没有自动闭环机制。

---

### 5. 反作弊机制缺失

**位置**: `FitnessLandscape.cs:117` 和 `SimulatedAnnealer.cs:174`

Fitness 评估是各维度加权求和 (`Reliability + CostEfficiency + Speed + Safety`)，无惩罚机制。如果系统通过降低 Quality 来换取 Cost 降低，总分数可能上升——低分维度被高分维度冲淡了。`FindBest` 不检查 Pareto 支配关系来保护质量，完全是简单求和。

**判断**: 系统可能进化出"低成本低质量"策略并以之为优。Fitness 缺少约束性惩罚（如 Quality 低于基线时的指数加权惩罚）。

---

### 6. 硬编码魔法数字分布图

| 文件 | 行号 | 数值 | 上下文 |
|------|------|------|--------|
| `BootstrapTeacher.cs` | 40-45 | 2000, 0.85, 1000, 0.95, 0.10, 0.02 | 全部 const |
| `ArchitectLoop.cs` | 218 | 0.7 | Risk 阈值 |
| `ArchitectLoop.cs` | 225 | 0.05 | Improvement 阈值 |
| `SimulatedAnnealer.cs` | 60 | 0.05, 0.15 | 突变强度范围 |
| `SimulatedAnnealer.cs` | 165-166 | 0.15×2, 0.20, 0.25×2, 0.50, 0.55 | 评估权重 |
| `ParetoRouter.cs` | 53, 380 | 0.40, 0.05 | Jitter 阈值, 矩阵初始化 |

---

## 🔴 红队测试结果

### "自毁按钮"测试
如果 GenePool 产生"删除所有 rules/*.md"的基因：
- **`SemanticDiffAgent`**: ❌ 不存在
- **`CounterfactualGate`**: ❌ 只评估路由行为偏移，不检查文件操作
- **`ArchitectLoop.DeployAsync`**: ❌ switch 分支不处理文件删除操作，提案只是恰好没有对应的枚举值

**结论**: 无任何防护。只不过当前 `ArchitectureAction` 枚举里恰好没有删除操作。

### "无限循环"测试
Architect 连续 10 次生成的代码都有 Bug 被 Rollback 时：
- `ArchitectLoop.cs:240-248`: 调用 `_teacher.ResetAsync()` + `FeedCuriosityBudgetAsync(50.0)`
- 之后——**继续尝试**
- 不暂停 Architect、不通知人类、不降低探索率

**结论**: 答案 **A —— 继续尝试 (僵尸行为)**。

### "相变僵局"测试
如果 Accuracy 永远是 0.84（TeachingAccuracyThreshold = 0.85）：
- 系统将**永远卡在 Teaching 阶段**
- 没有自动调整阈值的机制
- 唯一出路是 `ForceAdvancePhaseAsync`——需要外部人工干预

**结论**: 系统不具备自适应阈值调整能力。

---

## 🏁 最终裁定

**驳回**

**一句话审判**: 这次修复是 **"精致的补丁艺术"**——每个函数体内都有代码了，表象上"肉"是有的；但事件总线是断的、阈值是编译时常量、Gene 仍是字符串玩具、且不存在任何文件系统级的安全防护。系统停止了"空转"，但并未开始真正的自我进化——它只是从僵尸变成了西西弗斯——有力气推石头了，但石头永远到不了山顶。

---

## 📋 修复优先级建议

| 优先级 | 问题 | 修复建议 |
|--------|------|-----------|
| **P0** | 事件总线断路 | 在 DI 注册时注入 `teacher.CoordinationPublisher = scheduler.Publish` |
| **P0** | 硬编码阈值 | 将 `TeachingQuota` 等迁移为可配置属性，加入 `ArchitectureAction` 枚举 |
| **P1** | Gene 结构化 | 引入 `TargetModule`/`OperationType`/`Parameters` 结构替代纯字符串 |
| **P1** | 持久化闭环 | `DeployTopGenesAsync` 后自动调用 `PersistRulesAsync` |
| **P2** | 反作弊机制 | 在 Fitness 计算中加入 Quality 惩罚项，低于基线时指数衰减 |
| **P2** | 破坏性操作防护 | 实现 `SemanticDiffAgent` 或扩展 `CounterfactualGate` 检查文件操作 |
| **P3** | 自适应阈值 | 在 `AdvancePhaseIfReadyAsync` 中加入阈值松弛逻辑（如连续 5 轮未达标则放宽 0.02） |
