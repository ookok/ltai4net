# LTAI V0.6 "CPS-Evolve" — 架构文档

## 版本信息
- 版本: V0.6 "CPS-Evolve" + MACI 协调层
- 状态: **已实施**
- 前身: V0.56 "Production Hardening"
- 构建: 0 errors, 31 warnings
- 测试: 549 passed, 0 failed, 1 skipped

---

## 1. 架构总览

```
                        ┌─ MicroKernel (9 原语, 564行) ────────────┐
                        │  Execute / ReadFile / WriteFile / GitOp    │
                        │  HttpRequest / InvokeSkill / QueryMemory   │
                        │  ScheduleAsync / CancelScheduleAsync       │
                        │                                           │
                        │  ┌─ CPS 热路径 ──────────────────────────┐ │
                        │  │                                      │ │
  查询                  │  │ L0IntentClassifier (9 rules/*.md)     │ │
    ↓                   │  │        ↓                             │ │
  LivingTreeSystem      │  │ ParetoRouter (768→3 投影, ~308行)    │ │
  (270行, -62%)         │  │        ↓                             │ │
    │            ↘      │  │ LoopTrapDetector (4种检测, ~240行)   │ │
    │             ↘     │  │        ↓                             │ │
    ↓              ↘    │  │ confidence≥0.6 → CPS 快速路径        │ │
  ChatAsync      Stream │  │ confidence<0.6 → ReAct 循环          │ │
  CPS→L2→DNA    CPS→   │  │        ↓                             │ │
                ReAct→ │  │ L2云模型 (deepseek-v4-pro, 4096tk)   │ │
                L2     │  └──────────────────────────────────────┘ │
                        │                                           │
                        │  ┌─ 进化层 (离线, 每小时) ──────────────┐ │
                        │  │ BootstrapTeacher (3阶段自举, ~239行) │ │
                        │  │ GenePool (自然选择, ~341行)           │ │
                        │  │ SimulatedAnnealer (Metropolis, ~302行)│ │
                        │  │ ArchitectLoop (闭环, ~543行)          │ │
                        │  │ CounterfactualGate (影子部署, ~198行) │ │
                        │  │ GeneToRule (基因→前沿, 内嵌)          │ │
                        │  └──────────────────────────────────────┘ │
                        │                                           │
                        │  ┌─ MACI 协调层 ────────────────────────┐ │
                        │  │ CoordinationScheduler (事件总线)      │ │
                        │  │ RecursiveCausalAudit (推理链验证)     │ │
                        │  │ SemanticAnchor (UCCT相变锚定)         │ │
                        │  │ EvolutionLoopHostedService (定时驱动)  │ │
                        │  └──────────────────────────────────────┘ │
                        │                                           │
                        │  ┌─ 记忆层 ─────────────────────────────┐ │
                        │  │ MemoryGraph (4层层次图, ~250行)       │ │
                        │  │ DualRouteRetriever (S1相似+S2遍历)    │ │
                        │  │ ContextHub.RegisterDualRouteMemory     │ │
                        │  └──────────────────────────────────────┘ │
                        └───────────────────────────────────────────┘
```

**三层架构:**
1. **执行层** (L0 ONNX, 热路径): CPS → ParetoRouter → L2 直调，<50ms 决策
2. **进化层** (L1/L2, 离线): BootstrapTeacher + GenePool + ArchitectLoop，每小时运行
3. **协调层** (C#, 事件驱动): CoordinationScheduler → 组件间通知 + 推理链验证 + 相变锚定

---

## 2. 热路径 (ChatAsync / StreamChatAsync)

### ChatAsync
```
查询 → DNA 安全检查
     → CPSProcessingService.ProcessAsync
         ├─ L0IntentClassifier (rule/*.md, 9领域)
         ├─ LoopTrapDetector.Check (4种陷阱检测)
         ├─ ParetoRouter.Decide (3D前沿)
         ├─ confidence≥0.6 → CPS 快速路径
         └─ confidence<0.6 → ProcessTypedAsync
                               → L2云模型 → DNA输出安全
```

### StreamChatAsync
```
查询 → DNA 安全检查
     → CPSProcessingService
         ├─ confidence≥0.6 → yield CPS响应
         └─ confidence<0.6 → ContextHub (DualRouteRetriever)
                               → ReActLoopOrchestrator (工具循环)
                               → L2 直接流式 (兜底)
```

---

## 3. ReAct 循环 (已从死代码复活)

`ReActLoopOrchestrator.cs` (600行) 在简化时被抽离但未重新接入。V0.6.1 已重新集成:

- **接入点**: StreamChatAsync — CPS 低置信度 → ReAct 回退
- **能力**: 多步推理-行动-观察、工具调用链、迭代纠错、中间状态传递
- **DI**: 自动注入 (AddSingleton，LivingTreeSystem 构造参数自动解析)

---

## 4. 记忆系统 (Mnemis 双路径)

```
MemoryGraph (4层层次图)
  ├─ Layer 0: detail  (基础事实)
  ├─ Layer 1: summary (摘要节点)
  ├─ Layer 2: concept (概念抽象)
  └─ Layer 3: domain  (领域根节点)

DualRouteRetriever
  ├─ System-1: 嵌入相似度检索 (L0 ONNX)
  └─ System-2: L1模型全局推理筛选

接入点: ContextHub.RegisterDualRouteMemory
         → 替换旧 DualMemoryStore 简单关键词匹配
         → 优先于 dualMemory (dualRouteRetriever 存在时跳过旧存储)
```

---

## 5. MACI 协调栈

三组件实现协调物理学:

| 组件 | 来源 | 功能 |
|------|------|------|
| `CoordinationScheduler` | MACI 论文 | 事件驱动组件通知总线，替代固定计时器 |
| `RecursiveCausalAudit` | RCA 论文 | 推理链自洽性验证 (因果关系锚点检测) |
| `SemanticAnchor` | UCCT 论文 | 相变驱动路由锁定 (ρ_d - d_r + γlogk) |

**自动接线规则:**
- 自举阶段推进 → 触发基因进化 + 退火
- 基因部署 → 触发架构师审查
- 循环陷阱检测 → 提升探索预算 + 注入抖动
- 架构师提案 → 2分钟去抖动保护

---

## 6. 五子系统 (全部已实施)

| 子系统 | 文件 | 行数 | 状态 |
|--------|------|------|------|
| ParetoRouter | `ParetoRouter.cs` | 308 | L0 ONNX 3D前沿 + 影子路由 |
| BootstrapTeacher | `BootstrapTeacher.cs` | 239 | 教学→影子→自治 3阶段 |
| GenePool | `GenePool.cs` | 341 | 自然选择 + 领域隔离 |
| SimulatedAnnealer | `SimulatedAnnealer.cs` | 302 | Metropolis退火 + GeneToRule |
| ArchitectLoop | `ArchitectLoop.cs` | 543 | 观察→诊断→提案→部署闭环 |

---

## 7. 删除的旧链 (已从 LTAI 根除)

| 文件 | 行数 | 替代方案 |
|------|------|---------|
| `L1L2DuplexRouter.cs` | 980 | FunctionalTokenRouter (Atlas论文) |
| `MoERouter.cs` | 221 | StructureAwareRouter (聚类路由) |
| `CostAwareRouter.cs` | 205 | BudgetTracker (硬上限) + 降级链 |
| `ModelDispatchService.cs` | 185 | MultiProviderChatClient.GetDegradedModel |

**已知剩余缺口:**
- CostAwareRouter: 无每请求三难优化 (仅硬上限阻断) — 低优先级
- ModelSoup: MoE专家集成评分已移除 — 低优先级

---

## 8. 基础设施矩阵

| 组件 | 状态 | 文件 |
|------|------|------|
| MicroKernel (9原语) | ✅ 活跃 | `MicroKernel.cs` (564行) |
| 多语言解耦 | ✅ | `ProjectSpec.cs` + 7 ToolchainPresets |
| 质量门控 | ✅ | `BackpressurePipeline` (Lint/Typecheck/Test/Review) |
| DNA 安全 | ✅ | ChatAsync + StreamChatAsync 入口 |
| 记忆持久化 | ✅ | `MemoryGraph` + `DualRouteRetriever` |
| 定时进化 | ✅ | `EvolutionLoopHostedService` (2min/5min/10min) |
| 工作树隔离 | ✅ | `WorktreeOrchestrator` + libgit2sharp |
| 测试覆盖 | ✅ | 16 CPS + 12 LoopTrap + 549总通过 |

---

## 9. 组件清单 (完整)

```
src/LTAI.Core/Governors/
  MicroKernel.cs           564   9原语 + 电路断路器 + 审计日志
  ParetoRouter.cs          308   3D前沿 + 768→3投影 + 影子路由
  BootstrapTeacher.cs      239   3阶段自举 + 好奇心预算
  GenePool.cs              341   自然选择 + 领域隔离 + 交叉/变异
  SimulatedAnnealer.cs     302   Metropolis退火 + GeneToRule
  ArchitectLoop.cs         543   观察→诊断→提案→部署
  CounterfactualGate.cs    198   影子部署 + JS散度 + 行为向量
  CPSProcessingService.cs  240   统一执行 + LoopTrap集成
  LoopTrapDetector.cs      240   4种检测 (精确/语义/周期/停滞)
  CoordinationScheduler.cs 210   事件驱动协调总线
  RecursiveCausalAudit.cs  225   推理链自洽性验证
  SemanticAnchor.cs        180   UCCT相变锚定 (ρ_d, d_r, γlogk)
  MemoryGraph.cs           250   4层层次图 (detail→summary→concept→domain)
  DualRouteRetriever.cs    240   System-1相似 + System-2层次遍历
  WeightSubspaceAnalyzer.cs 449  PCA + Grassmann距离
  RuleLoader.cs            145   .md规则加载 (9 rules/*.md)
  L0IntentClassifier.cs    120   关键词+正则匹配分类器

src/LTAI.AI/Governors/
  LivingTreeSystem.cs      270   (简化 -62%)  CPS→ReAct→L2 3入口
  ReActLoopOrchestrator.cs 600   多步推理循环 (已复活接入)
  ContextHub.cs            185   + RegisterDualRouteMemory
  ContextHubBuilder.cs     220   + dualRouteRetriever参数

测试 (549通过, 1跳过):
  CPSEvolveIntegrationTests.cs   8 test
  CPSEvolvePipelineTests.cs      8 test
  LivingTreeSystemHotpathTests.cs 10 test
  MicroKernelTests.cs            12 test
  LoopTrapDetectorTests.cs       12 test
  ProjectSpecTests.cs             6 test
  RuleLoaderTests.cs             10 test
```

---

## 10. 成本模型

| 组件 | 层 | 日调用量 | 日成本 |
|------|-----|---------|--------|
| ParetoRouter | L0 ONNX | 10000 | $0 |
| L0IntentClassifier | L0 Keyword | 10000 | $0 |
| LoopTrapDetector | C# | 10000 | $0 |
| CPS (低置信回退) | L2 Pro | ~1000 | ~$0.10 |
| GenePool 进化 | L2 Pro | 3次/天 | $0.06 |
| ArchitectLoop | L2 Pro | 1次/小时 | $0.48 |
| ReAct 循环 | L2 Pro | ~200次/天 | ~$0.02 |

**日总成本**: ~$0.66 (Phase 3自治阶段)
**全L2对比**: $200/天
