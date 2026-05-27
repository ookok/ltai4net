# LTAI Agent OS — 架构文档 (Post-Audit)

## 概览

LTAI 是严格映射为现代操作系统层级模型的 Agent 框架：由底向上的生命堆叠。

经过 L0-L5 全层审计 + 8 条核心主线审计 + 10 分路线图冲刺，当前状态：
**层级隔离完整，8 项核心能力全部可用，55 个文件修改，0 构建错误。**

## 6 层架构

| 层级 | 传统 OS | Agent OS | 核心组件 | 审计后评分 |
|:---|:---|:---|:---|:---|
| **L0** | HAL | 微内核层 | `IMicroKernel` (11 原语 + CapToken + 配额) | 🟢 8/10 |
| **L1** | 驱动层 | 感知与执行层 | `SkillSystem`, `MemoryGraph`, `MultimodalOrchestrator` | 🟢 7.5/10 |
| **L2** | 系统服务 | 运行时与协调层 | `CoordinationScheduler`, `WorktreeManager`, `EvolutionLoop` | 🟢 8/10 |
| **L3** | 子系统 | 认知与决策层 | `ParetoRouter`, `RecursiveCausalAudit`, `L0IntentClassifier` | 🟢 8.5/10 |
| **L4** | 会话管理 | 进化与治理层 | `GenePool`, `ArchitectLoop`, `SemanticDiffAgent` | 🟢 8/10 |
| **L5** | 用户应用 | 智能体应用层 | `IAgent` (CodeAgent/ChatAgent/EIAAgent/ReasoningAgent) | 🟢 8/10 |

## 层间契约

```
上层可调用下层  ✅  (L4 可指挥 L0 写文件)
下层不感知上层  ❌  (L0 不知道 L4 的存在)
同层事件解耦    📡  (L3 ParetoRouter → CoordinationScheduler → L4 GenePool)
安全贯穿全层    🔒  (PromptShield(L1) → SemDiff(L4) → CapToken(L0) → Audit(all))
```

## 8 条核心主线能力矩阵

| 主线 | 核心问题 | 关键组件 | 评分 |
|------|----------|----------|:---:|
| **P1 感知** | 世界发生了什么？ | HybridIntentRouter, MultimodalOrchestrator, PromptShield, ContextMoE | 7.5 |
| **P2 规划** | 怎么完成目标？ | HTNPlanner, UniversalOrchestrator(5模式), TaskQueue DAG, Plan B | 7.0 |
| **P3 工具** | 需要借助什么？ | SkillRegistry(40+), ToolService(120+), MarkdownToolExecutor, Compose | 9.0 |
| **P4 记忆** | 以前见过吗？ | ContextMoE(5层), KnowledgeBase, MemoryGraph, KnowledgeGraph | 8.5 |
| **P5 决策** | 选 A 还是 B？ | ParetoRouter(Q/S/C 3D), BootstrapTeacher, MCTS Reasoning, CausalAudit | 8.5 |
| **P6 反馈** | 做得怎么样？ | AgenticLoop, BackpressurePipeline, DebugLoop, PartStreamStore | 8.3 |
| **P7 进化** | 下次更好吗？ | GenePool(GA), SimulatedAnnealer, FederatedLearning, ExperimentAnalyzer | 7.5 |
| **P8 安全** | 会伤害谁吗？ | 3重命令阻止, PolicyAsCode(16规则), CapToken, 全链路审计, HITL | 9.2 |

## L0: 微内核层

**11 个原语**: Execute, ReadFile, WriteFile(原子tmp+rename), GitOp, HttpRequest, InvokeSkill, QueryMemory, Schedule, AdjustParameter, LoadGene/UnloadGene, Snapshot/Restore

**安全机制**:
- 路径沙箱: 9 允许路径 + .git 阻止 + per-niche 隔离
- 网络围栏: 8 允许域名 + metadata IP 阻止 + per-niche 覆盖
- 命令白名单: 20 允许命令 (dotnet/git/npm/python/docker/curl...)
- CapToken: HMAC 签名票据 (subject/permission/path/TTL) + 吊销列表
- 熔断器: 10 连续失败 → 30s cooldown → git revert + Teacher reset
- 资源配额: 并发进程 ≤4, 总写入 ≤100MB, 并发操作 ≤16, 单文件 ≤10MB
- 全链路审计: ConcurrentQueue<KernelAuditEntry> (1000 条 FIFO, 风险评分默认 0.0)

## L1: 感知与执行层

**技能系统**: SkillRegistry (40+ .md skills, marketplace, 版本历史, 演进统计)
**记忆系统**: MemoryGraph (4 层 detail→summary→concept→domain, Search API, 自动重要性, 7 天过期自主修剪)
**多模态**: OCR(RapidOCREngine ONNX) + Vision(VisionAnalyzer) + TTS(3 引擎) + STT(Whisper + SpeechEngine)
**工具桥接**: MdToolBridge (`.md` 工具优先, C# handler 回退)
**三层命令阻止**: DangerousCommands(8) + BlockedShellPatterns(32) + UnifiedSafetyGate(6 regex)

## L2: 运行时与协调层

**自主心跳**:
- EvolutionLoopHostedService: 2/5/10 分钟循环 (evolution/architect/deploy)
- CPS 健康检查: 每 15 分钟验证路由管道
- MemoryGraph 修剪: 每 10 分钟清理过期节点
- SelfHealer 健康检查: 已注册(跨项目引用限制, 待接线)

**事件总线**: CoordinationScheduler (100ms 轮询, 12 事件类型, 4 引导规则)
**Worktree**: GitWorktreeManager (完整 CRUD) + WorktreeCleanupService (30 分钟自主清理)
**回压质量**: BackpressurePipeline (lint→typecheck→test→review, 3 retry)

## L3: 认知与决策层

**ParetoRouter**: Quality/Speed/Cost 3D Pareto 前沿, 基因驱动种子, 路由锁定(防振荡), 10% 影子路由, 投影矩阵每 1000 决策刷新
**意图分类**: L0IntentClassifier (关键字+正则, 中英文 fallback, ClassifyWithConfidence)
**因果验证**: RecursiveCausalAudit (因果锚点 + 可量化效果检测, 接入 CPS + ArchitectLoop)
**反事实**: CounterfactualGate → ArchitectLoop (Risk>0.3 自动触发)
**MCTS 推理**: ReasoningAgent (UCB1, 深度 5, token 预算 8000, 自一致性采样, 记忆化)
**语义锚点**: SemanticAnchor (AnchoredLabels 监控接入 CPS)
**L3 SLA**: 决策超时 >50ms 告警

## L4: 进化与治理层

**基因进化**: GenePool (精英/交叉/变异, 8 操作类型, niche 分享, RemoveGene)
**模拟退火**: SimulatedAnnealer (Metropolis 准则, 指数冷却 ×0.95)
**架构治理**: ArchitectLoop (22 动作, L2 LLM 诊断+提案, 3 重安全闸: Counterfactual + SemanticDiff + HITL)
**联邦学习**: FederatedLearningService + InMemoryFederatedTransport (跨实例技能/权重共享)
**实验分析**: ExperimentAnalyzer + CrossRunEvolutionStore (LiteDB, 30 天半衰期)
**OnnxML**: OnnxParallelEngine + OnnxModelPipeline + ToolRecommender + IidChangePointDetector (全部 DI 注册)
**提示词进化**: MultiPolicyTrainer + SharedReplayBuffer + GrpoPromptOptimizer

## L5: 智能体应用层

**IAgent 接口**: AgentId/Niche/Description/IsActive/HandleAsync/ActivateAsync/DeactivateAsync
**IAgentFactory**: FactoryId/SupportedNiches/CreateAsync
**4 个 Agent**: CodeAgent + ChatAgent + EIAAgent + ReasoningAgent (全部继承 BaseAgent, RunAsync 调用)
**AgentAdapters**: HandleAsync 调用真实 agent.RunAsync (非桩)
**AgentFactories**: 4 个工厂全部 DI 注册
**生命周期**: DeployAgent/UndeployAgent/HotSwapAgent (通过 CoordinationScheduler 发布事件)
**BootstrapTeacher**: Teaching(1h)→Shadowing(2h)→Autonomous 三阶段, 滞停处理, 超时保护

## 安全体系

| 层 | 机制 | 说明 |
|----|------|------|
| L1 | PromptShield | 10 注入模式 + 5 输出过滤 |
| L1 | PersonaDriftDetector | 人格一致性监控 |
| L5 | RLVRMonitor | 上升-下降模式追踪 |
| L0 | CapToken | HMAC 能力安全 |
| L0 | Sandbox | 路径/网络/命令三重隔离 |
| L0/L1 | 命令阻止 | 3 层独立阻止 (8+32+6 模式) |
| L4 | SemanticDiffAgent | 18 危险基因模式 |
| L4 | CounterfactualGate | 影子路由对比 |
| L4 | HITL | Risk>0.7 提案需人工审批 |
| ALL | PolicyAsCode | 16 规则 + RETE 引擎 + FileSystemWatcher 热加载 |
| ALL | Audit Trail | MicroKernel + ParetoRouter + ArchitectLoop + PartStreamStore |

## 审计修复清单

### V0.7 Gap 修复 (commit 1)
- CapToken 吊销列表, Worktree Niche, ParetoRouter 基因驱动, IAgent/IAgentFactory DI

### L0-L5 层审计修复 (commit 2)
- L0: 原子写, 命令白名单, 资源配额, 快照扩展, 审计评分
- L1: 命令沙箱绕过, 环境感知技能修复, 记忆系统统一, SkillLoader 错误隔离
- L2: CoordinationScheduler 自主启动, CPS 健康检查, 健康监控
- L3: RecursiveCausalAudit 接线, 意图分类增强, 语义锚点监控, L3 SLA
- L4: 因果验证, Bootstrap 超时, Agent 适配器修复
- L5: HandleAsync 非桩, Agent 生命周期事件

### 8 条主线审计修复 (commit 3)
- P1: Whisper+FFmpeg DI 注册
- P2: Parliament/Sequential 模式, Plan B, TaskQueue DI
- P3: 并发限流, ToolMethod 权限, 指数退避重试
- P4: PruneStaleNodes 自主触发, ChatAgent 记忆注入, Search API
- P5: 因果锚点增强, MCTS 记忆化, SemanticVerify 接线, 自一致性
- P6: AgenticLoop 连续失败升级策略
- P7: InMemoryFederatedTransport, OnnxML 子系统 DI
- P8: HITL 高风险提案拦截

### 10 分路线图冲刺 (commit 4)
- STT→Agent 桥接, UnifiedPlanningPipeline 接线
- Service 参数白名单, Compose 超时, 工具依赖验证
- 自动重要性增长, ParetoRouter 投影刷新
- OnnxML 模型 DI, PolicyAsCode 热加载
- HITL 反射接线

## 开发者上手路径

### 最小理解集 (先看这 5 个文件)

| 文件 | 作用 | 为什么先看 |
|------|------|-----------|
| `src/LTAI.Core/Governors/MicroKernel.cs` | L0 微内核 (11 原语) | 所有操作的唯一入口 |
| `src/LTAI.Core/Governors/CPSProcessingService.cs` | 中央路由处理器 | 理解"查询→决策→执行"全链路 |
| `src/LTAI.Core/Governors/ParetoRouter.cs` | 3D Pareto 路由决策 | 理解路由选择逻辑 |
| `src/LTAI.Agent/MAF/AgenticLoop.cs` | Read→Think→Edit→Run→Observe | 理解代码执行循环 |
| `src/LTAI.Agent/Skills/Runtime/SkillRuntime.cs` | 技能执行管道 | 理解工具调用链 |

### 架构复杂度地图

```
复杂度低 ↓                                       复杂度高 ↓
MicroKernel (11 primitives)     ParetoRouter (3D + shadow + lock)
SkillRegistry (CRUD)            ArchitectLoop (22 actions + 3 safety gates)
ChatAgent (conversation)        GenePool (GA + crossover + niche sharing)
CoordinationScheduler (events)  DebugLoop (error analysis + fix generation)
```

### 常见调试入口

- **查询路由异常**: 检查 `ParetoRouter.Decide` → 查看 `CPSProcessingService.ExplainLastDecision()`
- **Agent 不响应**: 检查 `CoordinationScheduler.IsRunning` → `CoordinationScheduler.GetHealthReport()`
- **工具调用失败**: 检查 `SkillStepExecutor.RunShellAsync` → 查看 `BlockedShellPatterns` 是否误拦截
- **记忆不注入**: 检查 `MemoryFilesService.RetrieveRelevant` → `ChatAgent.ExecuteLogicAsync`
- **进化停止**: 检查 `EvolutionLoopHostedService` 是否运行 → `GenePool.Evolve` 是否被调用

### 已知复杂度区域

| 区域 | 复杂度 | 原因 |
|------|--------|------|
| ArchitectLoop | 🔴 高 | 22 种动作 + LLM 诊断/提案 + 3 重安全闸 |
| DebugLoop | 🔴 高 | 错误分析 + 策略匹配 + 自适应温度 + git 回滚 |
| GenePool | 🟡 中 | 8 种基因操作 + niche 分享 + 交叉变异 |
| ParetoRouter | 🟡 中 | 3D 投影 + 路由锁定 + 影子路由 + 基因驱动 |
| 事件系统 | 🟡 中 | 12 种事件类型 + 4 条引导规则 + 100ms 轮询 |
| MicroKernel | 🟢 低 | 11 个独立原语, 无业务逻辑 |

## 性能特征

| 指标 | 典型值 | 说明 |
|------|--------|------|
| L3 决策延迟 | < 50ms | ParetoRouter 纯矩阵运算, SLA 告警阈值 |
| L1 推理延迟 | 1-5s | Qwen2.5-1.5B ONNX 本地推理 |
| L2 推理延迟 | 2-10s | DeepSeek-v4 / Qwen-Max API 调用 |
| 并发原语 | ≤ 16 | MicroKernel SemaphoreSlim |
| 并发进程 | ≤ 4 | MicroKernel 进程配额 |
| 内存节点 | ≤ 10,000 | MemoryGraph 硬上限 |
| 基因池 | ≤ 200 | GenePool maxPopulation |
| 审计条目 | ≤ 1,000 | 环形 FIFO 淘汰 |

### 成本模型

ParetoRouter 的 Quality/Speed/Cost 三维路由决策中：
- **reflex**: 0 token, 0 成本, ~1ms (纯关键字/缓存)
- **local**: ~500 tokens, 极低成本, ~50ms (L0 ONNX 模型)
- **L1**: ~2,000 tokens, 低成本, 1-5s (本地 Qwen ONNX)
- **L2**: ~8,000 tokens, API 成本, 2-10s (云端 DeepSeek/Qwen)

BootstrapTeacher 三阶段自适应路由确保：Teaching 阶段 100% L2 (学习), Shadowing 阶段 10% L2 (验证), Autonomous 阶段 2% L2 (抽查)。

