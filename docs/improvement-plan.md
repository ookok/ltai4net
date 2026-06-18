# LTAI 4 Net — 深度改进计划

> 生成日期：2026-06-18
> 基于全面代码审查（600 src .cs, 921 tests, 14 projects）

---

## Phase 1: Critical (0–2 weeks)

### P1 — DI Registration 模块化重构

**现状**：`ServiceCollectionExtensions.cs` 单文件 763 行，92 个 `AddSingleton`，57 个 `sp =>` lambda 工厂。零个 `AddScoped`。

**方案**：拆分为 6 个 static 扩展方法文件：

| 文件 | 迁移行数 | 负责模块 |
|------|---------|---------|
| `DI/CoreRegistration.cs` | ~30 | `KgStore`, `Glove50Embedder`, `AgentLookaheadRouter`, `ContractRegistry/Watcher`, `Reranker` |
| `DI/GraphAndExpertRegistration.cs` | ~50 | `KbGraph`, `CgGraph`, 7× `IExpertModule`, `ExpertRegistry/Router/FanOut/Aggregator`, `EntropyTracker`, `MemoryCompressor`, `FactExtractor` |
| `DI/WorkflowAndPipelineRegistration.cs` | ~60 | `DecisionTreeRouter`, `AgentWorkflows`, `MoAWorkflow`, `YAMLWorkflowRegistry/Watcher`, `PipelineRunner`, 6× pipeline steps |
| `DI/MemoryRegistration.cs` | ~40 | `PalaceStore`, `FallbackRetriever`, `FeedbackTracker`, `MemoryConsolidation`, `MemoryRefinery`, `PalaceStore`, `SessionMemoryExtractor` |
| `DI/ToolAndServiceRegistration.cs` | ~50 | `TaskQueue`, `DocumentIndexer/QueueWorker`, `SnippetStore`, `QuestionService`, `SkillEvolutionEngine`, `CodeChunkIndex`, `SeedER` |
| `DI/AgentRegistration.cs` | ~30 | `AgentRegistry`, `PromptLoader`, `AgentToolStore`, `ChatAgent`, `BudgetTracker`, `BackgroundJobService`, `DelegationContext` |

DI 顺序保留在 `AddLTAIAgent()` 中通过显式调用各方法维持：
```csharp
public static IServiceCollection AddLTAIAgent(this IServiceCollection services, out IReadOnlyList<string> registeredAgentNames)
{
    services.AddLTAIAgentCore();       // AgentToolStore, AgentRegistry, PromptLoader
    services.AddLTAIAgentDiGraph();    // KgStore, Glove50, LookaheadRouter, ContractRegistry
    services.AddLTAIAgentGraphAndExpert(); // KbGraph, CgGraph, Experts
    services.AddLTAIAgentWorkflows();  // Workflow, Pipeline
    services.AddLTAIAgentMemory();     // PalaceStore, MemoryConsolidation
    services.AddLTAIAgentTools();      // TaskQueue, Indexing, Skills
    services.AddLTAIAgentChat();       // ChatAgent, BudgetTracker
    // ... 返回 agentNames
}
```

### P2 — 消除 `AgentBuilder.s_serviceProvider` 全局 IServiceProvider

**现状**：`AgentBuilder.cs:55` — `internal static IServiceProvider? s_serviceProvider`。15 处静态方法接受 `IServiceProvider` 参数。

**方案**：
1. `CgGraph` / `ContractRegistry` 访问：`s_serviceProvider?.GetService<CgGraph>()` → 从 `BuildAgentImpl` 参数传入
2. `PatchEditTool.ImpactAnalyzer`：通过构造函数传入 `CgGraph`
3. `AgentModeObserver`：4 个静态 mutable 属性 → DI singleton

### P3 — AgentBuilder 静态成员实例化

**现状**：5 个 `static readonly` 实例：`s_lsp`, `s_mmapCache`, `s_mmapProvider`, `s_writeBuf`, `_envLock/_envApplied`

**方案**：全部移至 DI。`s_lsp` → `services.AddSingleton<LspLanguageManager>()`。`s_mmapCache/provider/writeBuf` → DI Singleton。

---

## Phase 2: High Priority (2–4 weeks)

### P4 — PipelineRunner 改为 DI 自动收集

**现状**：构造函数 6 个 nullable 参数；`ChatAgent.cs` 有 `?? new PipelineRunner(grammarCheck: _grammarCheck)` 不一致 fallback。

**方案**：改为 `IEnumerable<IPipelineStep>` DI 注入 + 声明式排序。

### P5 — MultiProviderChatClient 拆分（1064 行 → 5 文件）

```
MultiProviderChatClient.cs
  ├── ProviderClientManager.cs     — _clients 字典、Register/Unregister、降级链
  ├── CircuitBreakerManager.cs     — 熔断器、ProviderStats、冷却
  ├── ResponseCacheManager.cs      — MemoryCache、TTL、大小限制
  ├── OpenAIChatClientFactory.cs   — 从原文件抽出的静态工厂
  └── MultiProviderChatClient.cs   — 仅核心 IChatClient 接口
```

### P6 — 22 个分散的 static ConcurrentDictionary 集中管理

评估每个缓存的共享需求与上限，改 Singleton DI 或添加过期策略。

---

## Phase 3: Medium Priority (4–8 weeks)

### P7 — 测试补充（核心模块覆盖率）

| 模块 | 当前 | 目标 |
|------|------|------|
| ExpertRouter / ExpertRegistry | ~0% | 30+ tests |
| Memory Providers (L0–L6) | ~0% | 40+ tests |
| AgentBuilder 工具矩阵 | ~5% | 20+ tests |
| ToolFilteringChatClient | ~0% | 15+ tests |

### P8 — 安全规则外置

`ShellSecurity.cs` 6 个静态字典 → `appsettings.json:LTAI:Security`，IOptions 注入。

### P9 — 子模块版本锁定 + CI 构建缓存

锁定 agent-framework 和 durabletask-dotnet 的 commit SHA。CI 中缓存 `dist/lib/maf/`。

### P10 — 集成测试环境（Testcontainers + Ollama）

---

## Phase 4: Low Priority

- P11: 30+ 环境变量统一到 `LTAIOptions`
- P12: 移除 `QualityGateStep.PassThreshold` 静态可写字段
- P13: Global Usings 整理
- P14: 模型下载完整性校验（SHA256）

---

## 路线图

```
Week 1-2:   P1 DI 模块化    P2 消除全局 IServiceProvider    P3 AgentBuilder 静态实例化
Week 3-4:   P4 PipelineRunner DI    P5 MultiProviderChatClient 拆分
Week 5-6:   P6 静态字典集中管理    P7 测试补充
Week 7-8:   P8 安全配置外置    P9 子模块锁定 + CI    P10 集成测试
Week 9+:    P11-P14 整洁项
```

总计约 **7 人周**。
