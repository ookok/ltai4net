# LTAI V0.7 — Agent OS 6 层架构

## 概览

LTAI V0.7 严格映射为现代操作系统的层级模型：由底向上的生命堆叠。

每一层对应生物神经系统的高级功能，层间遵循**不可逆调用原则**。

## 层级映射表

| 层级 | 传统 OS 类比 | Agent OS 名称 | 核心职责 | LTAI 组件 |
|:---|:---|:---|:---|:---|
| **L0** | 硬件抽象层 (HAL) | 微内核层 | 最小执行、资源仲裁、安全原语 | `IMicroKernel` (11 原语 + Capability Token) |
| **L1** | 驱动层 | 感知与执行层 | 连接外部世界 (API/工具/记忆) | `SkillSystem`, `MemoryGraph`, `SKillRegistry` |
| **L2** | 系统服务层 | 运行时与协调层 | 进程调度、事件总线、生命周期 | `CoordinationScheduler`, `WorktreeManager` |
| **L3** | 子系统层 | 认知与决策层 | 路由、推理、因果验证 | `ParetoRouter`, `RecursiveCausalAudit` |
| **L4** | 会话管理层 | 进化与治理层 | 基因突变、适应度评估、安全审查 | `GenePool`, `ArchitectLoop`, `SemanticDiffAgent` |
| **L5** | 用户应用层 | 智能体应用层 | 任务执行 (编码/数学/对话) | `IAgent` 实现: `CodeAgent`, `EIAAgent`, `ChatAgent`, `ReasoningAgent` |

## 层间契约 (Inversion of Control)

```
上层可调用下层  ✅  (L4 可指挥 L0 写文件)
下层不感知上层  ❌  (L0 不知道 L4 的存在)
同层事件解耦    📡  (L3 ParetoRouter 通过 CoordinationScheduler 通知 L4 GenePool)
```

## L0: 微内核层

### 现状 (V0.6 硬化后)
- 11 原语全部实现: ExecuteAsync, ReadFileAsync, WriteFileAsync, GitOpAsync, HttpRequestAsync, InvokeSkillAsync, QueryMemoryAsync, ScheduleAsync, CancelScheduleAsync, AdjustParameterAsync, LoadGeneAsync/UnloadGeneAsync/SnapshotAsync/RestoreAsync/Subscribe
- 沙箱化: ExecuteAsync 路径白名单验证 + SemanticDiffAgent 基因安全扫描
- 原子持久化: WriteFileAsync tmp+rename
- 网络围栏: AllowedDomains/BlockedDomains
- 熔断回滚: KernelCircuitBreaker + HandleRollbackAsync
- 体征采集: P50/P95/P99 + 成功/失败/超时计数
- 进化原语: AdjustParameter/Snapshot/Restore

### V0.7 新增: Capability Token (能力安全)

当前 `WriteFileAsync(string path, string content, ct)` 裸路径，任意调用者可写任意路径。

V0.7 引入 **Object-Capability 模型**：

```
IMicroKernel 新增:
  - string IssueCapToken(string subject, KernelPermission perm, string targetPath, TimeSpan ttl)
  - KernelResult WriteFileWithToken(string capToken, string content, CancellationToken ct)
  - bool RevokeCapToken(string capToken)
  - (bool Valid, string Subject, KernelPermission Perm, string Target, DateTime Expiry) ValidateCapToken(string capToken)
```

CapToken 是一个自包含的 HMAC 签名票据，包含:
- 签发主体 (e.g. "GenePool.mutant_abc")
- 授权权限 (e.g. Write + Read)
- 目标路径 (e.g. "skills/")
- 过期时间 (TTL)

`WriteFileWithToken` 校验 CapToken 后自动将写入限制在 `TargetPath` 子树内。
无效/过期/权限不匹配的 Token 返回 Fail("unauthorized")。

## L1: 感知与执行层

### V0.7 变更: Skill 内核调度

`SkillRegistry.Register()` 现在在注册时自动桥接到 `MicroKernel.InvokeSkillAsync()`：

```
IMicroKernel.skillHandler = async (skillName, input, ct) =>
    await SkillRegistry.RunAsync(skillName, input, ct);
```

所有 L5 智能体调用 Skill 时不再直接依赖 SkillSystem，而是通过微内核的 InvokeSkillAsync 原语，享受统一的审计追踪 + 权限校验。

## L2: 运行时与协调层

### V0.7 变更: Worktree 按 Niche 隔离

`GitWorktreeManager.CreateWorktree()` 现在接受 `niche` 参数，为每个进化小众 (e.g. "code", "eia") 创建独立 worktree。

```csharp
record WorktreeCreateResult {
    string Niche { get; init; }  // NEW
}
```

每个 worktree 的 `KernelSandboxConfig` 自动继承 niche 的 AllowedPaths/BLockedPaths。

## L3: 认知与决策层

### V0.7 变更: ParetoRouter 去硬编码

`ParetoRouter.AddSeedPoints()` 改为从 GenePool 加载当前最优基因作为种子点，而非硬编码 `seed_reflex/seed_local/seed_l1/seed_l2`。

```csharp
// V0.6 (hardcoded):
seeds = new[] { seed_reflex(Q=0.3,S=1.0,C=0.0), seed_local(Q=0.55,S=0.8,C=0.05), ... }

// V0.7 (gene-driven):
seeds = _genePool.SelectTopN(4).Select(g => new ParetoPoint {
    Id = $"gene_{g.Id}",
    Label = g.RouteLabel,
    Quality = (float)g.Fitness,
    Speed = g.RouteLabel switch { "reflex" => 1f, "L1" => 0.5f, _ => 0.5f },
    Cost = g.RouteLabel switch { "reflex" => 0f, "L1" => 0.15f, _ => 0.5f },
    Embedding = EmbedRule(g)
}).ToArray();
```

## L4: 进化与治理层

### V0.7 变更: ArchitecturalLoop 支持 IAgent 部署

`ArchitectureAction` 枚举新增:

```csharp
DeployAgent,        // 部署一个新 IAgent 实例 (gene 驱动)
UndeployAgent,      // 卸载一个 IAgent
HotSwapAgent,       // 热替换 Agent 实现 (零中断)
```

`DeployAgent` case: 读取 proposal.Parameters，通过 `IAgentFactory.CreateAsync()` 创建 IAgent，注入到 AgentPool。

## L5: 智能体应用层

### V0.7 新增: IAgent 插件化接口

```csharp
namespace LTAI.Core.Interfaces;

public interface IAgent
{
    string AgentId { get; }
    string Niche { get; }
    string Description { get; }
    bool IsActive { get; }
    Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct);
    Task ActivateAsync(CancellationToken ct);
    Task DeactivateAsync(CancellationToken ct);
}

public interface IAgentFactory
{
    string FactoryId { get; }
    string[] SupportedNiches { get; }
    Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct);
}
```

### IAgent 生命周期

```
GenePool 触发 DeployAgent → IAgentFactory.CreateAsync() →
  → IAgent 注入 AgentPool (NichedSlot) →
  → AgentPool.OnAgentDeployed 事件 →
  → CoordinationScheduler.RegisterAgent(agent)

GenePool 触发 UndeployAgent → IAgent.DeactivateAsync() →
  → AgentPool 移除 slot →
  → CoordinationScheduler.UnregisterAgent(agentId)
```

### 现有 Agent 迁移

| 现有类 | 新接口适配 |
|--------|-----------|
| `CodeAgent : BaseAgent` | 增加 `CodeAgentFactory : IAgentFactory`，`CodeAgentAdapter : IAgent` 包装 |
| `EIAAgent : BaseAgent` | 同理 `EIAAgentFactory` + `EIAAgentAdapter` |
| `ChatAgent : BaseAgent` | 同理 |
| `ReasoningAgent : BaseAgent` | 同理 |

Adapter 模式保持现有 `BaseAgent` / `Microsoft.Agents.AI` 继承链不变，IAgent 作为薄包装层统一管理生命周期。

## V0.7 文件清单

| 新增/修改 | 文件 | 内容 |
|-----------|------|------|
| 新增 | `docs/architecture-v0.7-agent-os.md` | 本文档 |
| 新增 | `src/LTAI.Core/System/KernelCapToken.cs` | CapToken 签发/校验/吊销 |
| 修改 | `src/LTAI.Core/Governors/MicroKernel.cs` | IssueCapToken + WriteFileWithToken + RevokeCapToken + ValidateCapToken |
| 新增 | `src/LTAI.Core/Interfaces/IAgent.cs` | IAgent + IAgentFactory 接口 |
| 新增 | `src/LTAI.Agent/Agents/AgentAdapters.cs` | CodeAgent/EIAAgent/ChatAgent/ReasoningAgent 的 IAgent 包装 |
| 新增 | `src/LTAI.Agent/Agents/AgentFactories.cs` | 4 个 Agent 的 IAgentFactory 实现 |
| 修改 | `src/LTAI.Core/Governors/ArchitectLoop.cs` | ArchitectureAction 新增 DeployAgent/UndeployAgent/HotSwapAgent |
| 修改 | `src/LTAI.Core/Governors/ParetoRouter.cs` | AddSeedPoints() 改为 gene-driven |
| 修改 | `src/LTAI.Agent/Workflows/GitWorktreeManager.cs` | CreateWorktree 接受 niche 参数 |
| 修改 | `src/LTAI.Agent/ServiceCollectionExtensions.cs` | DI: SkillRegistry → MicroKernel, AgentFactory 注册, Worktree niche 隔离 |

## 验收标准

1. `dotnet build` 0 errors
2. CapToken 签发→使用→过期→吊销 全链路可测试
3. IAgent 接口可被 Gene 驱动的 `ArchitectLoop.DeployAgent` 动态创建
4. `microkernel.WriteFileWithToken("令牌", "...")` 拒绝无效令牌
5. `ParetoRouter.AddSeedPoints()` 从 GenePool 读取种子（无硬编码）
