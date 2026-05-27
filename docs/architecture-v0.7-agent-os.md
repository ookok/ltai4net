# LTAI V0.7 — Agent OS 6 层架构

## 概览

LTAI V0.7 严格映射为现代操作系统的层级模型：由底向上的生命堆叠。

每一层对应生物神经系统的高级功能，层间遵循**不可逆调用原则**。

## 层级映射表

| 层级 | 传统 OS 类比 | Agent OS 名称 | 核心职责 | LTAI 组件 |
|:---|:---|:---|:---|:---|
| **L0** | 硬件抽象层 | 微内核层 | 最小执行、资源仲裁、安全原语 | `IMicroKernel` (11 原语 + CapToken) |
| **L1** | 驱动层 | 感知与执行层 | 连接外部世界 (API/工具/记忆) | `SkillSystem`, `MemoryGraph`, `SkillRegistry` |
| **L2** | 系统服务层 | 运行时与协调层 | 进程调度、事件总线、生命周期 | `CoordinationScheduler`, `GitWorktreeManager` |
| **L3** | 子系统层 | 认知与决策层 | 路由、推理、因果验证 | `ParetoRouter`, `RecursiveCausalAudit` |
| **L4** | 会话管理层 | 进化与治理层 | 基因突变、适应度评估、安全审查 | `GenePool`, `ArchitectLoop`, `SemanticDiffAgent` |
| **L5** | 用户应用层 | 智能体应用层 | 任务执行 (编码/数学/对话) | `IAgent` 实现 (CodeAgent, EIAAgent, ChatAgent, ReasoningAgent) |

## 层间契约

```
上层可调用下层  ✅  (L4 可指挥 L0 写文件)
下层不感知上层  ❌  (L0 不知道 L4 的存在)
同层事件解耦    📡  (L3 ParetoRouter 通过 CoordinationScheduler 通知 L4 GenePool)
```

## L0: 微内核层

### 11 原语 (IMicroKernel)

| 原语 | 方法签名 | 状态 |
|------|----------|------|
| Execute | `ExecuteAsync(command, ct)` | ✅ |
| Read File | `ReadFileAsync(path, ct)` | ✅ |
| Write File | `WriteFileAsync(path, content, ct)` | ✅ |
| Git Op | `GitOpAsync(opCode, args, ct)` | ✅ |
| HTTP Request | `HttpRequestAsync(req, ct)` | ✅ |
| Invoke Skill | `InvokeSkillAsync(name, input, ct)` | ✅ |
| Query Memory | `QueryMemoryAsync(query, topK, ct)` | ✅ |
| Schedule | `ScheduleAsync(id, cmd, interval, recurring, ct)` | ✅ |
| Cancel Schedule | `CancelScheduleAsync(id, ct)` | ✅ |
| Adjust Parameter | `AdjustParameterAsync(component, key, value, ct)` | ✅ |
| Gene Load/Unload | `LoadGeneAsync` / `UnloadGeneAsync` | ✅ |
| Snapshot/Restore | `SnapshotAsync` / `RestoreAsync` | ✅ |

### Capability Token (Object-Capability 模型)

```csharp
string IssueCapToken(subject, permissions, targetPath, ttl);       // ✅
Task<KernelResult> WriteFileWithToken(capToken, content, ct);      // ✅
bool RevokeCapToken(capToken);                                     // ⚠️ no-op stub
CapTokenInfo? ValidateCapToken(capToken);                          // ✅
```

`KernelCapToken` 使用 HMAC-SHA256 自签名票据，包含 subject / permissions / path / expiry。`WriteFileWithToken` 校验签名、权限、过期后限制写入 `TargetPath` 子树。

**DI 连线**: MicroKernel 以单例注册，通过 `skillHandler`/`gitHandler`/`memoryHandler` 委托桥接 L1 层组件，Niche 沙箱在启动时初始化 4 个隔离区 (code/eia/chat/reasoning)。

[MicroKernel DI 注册](src/LTAI.Agent/ServiceCollectionExtensions.cs:256-335)

## L1: 感知与执行层

- **Skill 桥接**: `SkillRegistry.RunAsync()` → `IMicroKernel.InvokeSkillAsync()` 委托链路完整。所有 L5 Agent 的 Skill 调用通过微内核享受统一审计追踪。
- **MemoryGraph**: 存在 (`src/LTAI.Core/Governors/MemoryGraph.cs`, 430 行)，通过 `memoryHandler` 委托注入 MicroKernel。
- **DI**: `SkillRegistry`, `SkillRuntime`, `KnowledgeBase` 均注册。

[Skill→MicroKernel 桥接](src/LTAI.Agent/ServiceCollectionExtensions.cs:281-324)

## L2: 运行时与协调层

- **CoordinationScheduler**: 存在 (`src/LTAI.Core/Governors/CoordinationScheduler.cs`, 266 行)，Publish 方法注入 `BootstrapTeacher.CoordinationPublisher`。
- **GitWorktreeManager**: 存在，`CreateWorktree(agentId, baseBranch, ct)` 为每个 agent 创建独立 worktree。`WorktreeCreateResult` 不支持 `Niche` 字段。
- **Niche 沙箱隔离**: `KernelSandboxConfig.NicheIsolation(workspaceRoot, niche, worktreePath)` 已实现并在 DI 中为 4 个 niche 调用 `SetNicheSandbox`。

[DI: Worktree + Scheduler](src/LTAI.Agent/ServiceCollectionExtensions.cs:256-375)

## L3: 认知与决策层

- **ParetoRouter**: 存在 (`src/LTAI.Core/Governors/ParetoRouter.cs`, 403 行)，支持 reflex/local/L1/L2 标签的 Pareto 前沿路由。
- **RecursiveCausalAudit**: 存在 (`src/LTAI.Core/Governors/RecursiveCausalAudit.cs`, 300 行)。
- **L0IntentClassifier**: 存在 (`src/LTAI.Core/Governors/L0IntentClassifier.cs`, 196 行)。

[ParetoRouter 构造函数](src/LTAI.Core/Governors/ParetoRouter.cs:57)

## L4: 进化与治理层

- **GenePool**: 存在 (`src/LTAI.Core/Governors/GenePool.cs`, 599 行)，maxPopulation=200。
- **ArchitectLoop**: 存在 (`src/LTAI.Core/Governors/ArchitectLoop.cs`, 830 行)，`ArchitectureAction` 枚举含 17 个动作。
- **SemanticDiffAgent**: 存在，DI 注册。

[DI: GenePool + ArchitectLoop + SimulatedAnnealer](src/LTAI.Agent/ServiceCollectionExtensions.cs:361-375)

## L5: 智能体应用层

- **IAgent 接口**: 存在 (`src/LTAI.Core/Interfaces/IAgent.cs`, 20 行) — `AgentId`, `Niche`, `Description`, `IsActive`, `HandleAsync`, `ActivateAsync`, `DeactivateAsync`。
- **IAgentFactory 接口**: 存在 — `FactoryId`, `SupportedNiches`, `CreateAsync(config, ct)`。
- **AgentAdapters**: 存在 (`src/LTAI.Agent/Agents/AgentAdapters.cs`, 4 个适配器)。
- **AgentFactories**: 存在 (`src/LTAI.Agent/Agents/AgentFactories.cs`, 4 个工厂)。

[IAgent + IAgentFactory 接口](src/LTAI.Core/Interfaces/IAgent.cs)

---

## 🔍 V0.7 代码对齐审计 (2026-05-27)

以下 7 项是审计发现的 **doc→code 不一致**。标注 ✅ 表示已验证实现，⚠️ 表示存在缺口。

### ⚠️ GAP-1: `ArchitectLoop.DeployAgent/UndeployAgent/HotSwapAgent` 未实现

`ArchitectureAction` 枚举不含这三个值。`search_content "DeployAgent|UndeployAgent|HotSwapAgent"` 在 `src/` 下零匹配。

- **影响**: Gene 驱动的 Agent 动态部署/热替换流水线不存在
- **证据**: [ArchitectLoop ArchitectureAction 枚举](src/LTAI.Core/Governors/ArchitectLoop.cs:44-61) — 17 个条目不含 DeployAgent 系列

### ⚠️ GAP-2: `ParetoRouter.SeedDefaultFrontier()` 仍硬编码

种子点仍为 `seed_reflex/seed_local/seed_l1/seed_l2` 硬编码，未从 `GenePool.SelectTopN()` 加载。`GenePool` 不注入 ParetoRouter，ParetoRouter 内部无任何 GenePool 引用。

- **影响**: 路由种子不随进化更新，Pareto 前沿初始化与基因进化脱钩
- **证据**: [SeedDefaultFrontier 方法](src/LTAI.Core/Governors/ParetoRouter.cs:386-402) — 5 个硬编码种子点

### ⚠️ GAP-3: `GitWorktreeManager.WorktreeCreateResult` 无 Niche 字段

`WorktreeCreateResult` 仅有 `Success/WorktreePath/Branch/Error` 四个字段。`search_content "Niche|niche" path:src/LTAI.Agent/Workflows/` 零匹配。

- **影响**: Worktree 不与进化 niche 关联，无法按 niche 继承 NicheIsolation 沙箱
- **证据**: [GitWorktreeManager WorktreeCreateResult](src/LTAI.Agent/Workflows/GitWorktreeManager.cs:20-26)

### ⚠️ GAP-4: `KernelCapToken.Revoke()` 是空操作

`Revoke()` 方法体为 `{ }`，无吊销列表、无持久化。签署的 Token 无法真正撤销。

- **影响**: 已签发的 CapToken 始终有效直到过期，无法主动吊销
- **证据**: [KernelCapToken.Revoke](src/LTAI.Core/System/KernelCapToken.cs:113-115)

### ⚠️ GAP-5: 新 `IAgentFactory` 实现未注册 DI

`CodeAgentFactory`/`EIAAgentFactory`/`ChatAgentFactory`/`ReasoningAgentFactory` 四个工厂类存在于 `AgentFactories.cs` 但未在 `ServiceCollectionExtensions` 中注册。DI 中仅注册了旧版 `IAgentFactory, AgentFactory` (来自 `src/LTAI.Agent/AgentFactory.cs`)。

- **影响**: 新版工厂为死代码，无法通过 DI 解析
- **证据**: [DI 注册: IAgentFactory → AgentFactory](src/LTAI.Agent/ServiceCollectionExtensions.cs:251)

### ⚠️ GAP-6: `IAgent` 适配器未注册 DI

`CodeAgentAdapter`/`EIAAgentAdapter`/`ChatAgentAdapter`/`ReasoningAgentAdapter` 四个适配器类存在于 `AgentAdapters.cs` 但未注册 DI。

- **影响**: 适配器为死代码；即使 GAP-1 修复，ArchitectLoop 也无法获取 IAgent 实例
- **证据**: [AgentAdapters.cs](src/LTAI.Agent/Agents/AgentAdapters.cs) — 4 个适配器，DI 零注册

### ⚠️ GAP-7: `ArchitectLoop` → `IAgent` 部署流水线完全未连线

综合 GAP-1 + GAP-5 + GAP-6：ArchitectureAction 无 DeployAgent 命令，新工厂/适配器未注册 DI，整个 "GenePool 触发 DeployAgent → IAgentFactory.CreateAsync() → IAgent 注入 AgentPool → CoordinationScheduler 注册" 流水线为文档描述但代码不存在。

- **影响**: L5 Agent 的插件化生命周期管理尚未实现
- **证据**: 上述三条 gap 的组合效应

### ✅ 已确认的实现

- MicroKernel 11 原语全部实现 + CapToken 4 方法
- `InvokeSkillAsync → SkillRegistry` 委托桥接已连通
- `KernelSandboxConfig.NicheIsolation` + `SetNicheSandbox` 已实现并在 DI 中初始化
- `MicroKernel.GenePool` / `.Teacher` 属性已从 DI 注入
- `SemanticDiffAgent`、`ArchitectLoop`、`GenePool`、`ParetoRouter`、`BootstrapTeacher`、`SimulatedAnnealer` 全部 DI 注册

### 修复路线图 (按依赖顺序)

1. **GAP-4** → `KernelCapToken.Revoke()` 加 `ConcurrentHashSet<string>` 吊销列表
2. **GAP-3** → `WorktreeCreateResult` 加 `Niche` 属性；`CreateWorktree` 方法签名加 `niche` 参数
3. **GAP-2** → `ParetoRouter` 构造函数加 `GenePool?` 参数；`SeedDefaultFrontier` 优先从 GenePool 加载
4. **GAP-6** → 在 DI 中注册 4 个 `IAgent` 适配器
5. **GAP-5** → 在 DI 中注册 4 个 `IAgentFactory` 实现
6. **GAP-1** → `ArchitectureAction` 枚举加 `DeployAgent/UndeployAgent/HotSwapAgent` 三个值；`ArchitectLoop` 实现对应的 case 分支
