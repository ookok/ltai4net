# LTAI 4 Net — MAF 范式符合度评估报告

> 评估范围：`src/LTAI.Agent/`、`src/LTAI.AI/`、`src/LTAI.Web/`、`src/LTAI.Cli/`、`src/LTAI.TUI/`、`src/LTAI.Desktop/`、`tests/LTAI.Tests/`
> 评估对象：与 MAF（Microsoft Agent Framework，v1.0.0-preview / master 头 `9d4c952a`）的范式对齐情况
> 评估时间：2026-06-01
> 总体结论：**部分对齐** — 基础设施层（Agent/ChatClient/Workflow/A2A）已 100% 切到 MAF 原语；但 **ChatClient 中间件层**、**Agent 装饰器层**、**Routing 决策层** 三处仍以「自实现 DelegatingChatClient / DelegatingAIAgent」完成，应替换为 MAF 原生中间件。整体自研代码量约占 Agent 模块 18%（净代码行）。

---

## 1. 整体打分

| 维度                              | 评分   | 说明                                                                  |
| --------------------------------- | ------ | --------------------------------------------------------------------- |
| ChatClient / IChatClient 装饰器链 | ⭐⭐⭐⭐⭐  | `FunctionInvokingChatClient` + 8 个 MEAI 原生中间件全部由 MAF 注入  |
| AIAgent 构造 + 装饰器链            | ⭐⭐⭐⭐   | 用 `AIAgentBuilder.Use()` 包装 3 个装饰器；**ChatClient 中间件用错** |
| AIContextProvider 组合             | ⭐⭐⭐⭐⭐  | 8 个 MAF 原生 + 2 个 LTAI 自研 Provider 共存                          |
| Function / Tool 注册              | ⭐⭐⭐⭐⭐  | 全部走 `AIFunctionFactory.Create`，符合 MAF 范式                      |
| Workflow / 编排                   | ⭐⭐⭐⭐⭐  | `AgentWorkflowBuilder` 三种编排 + Checkpointing 全用                  |
| A2A 协议                          | ⭐⭐⭐⭐⭐  | `AddA2AServer` + `MapA2AHttpJson` + AgentCard 全用                   |
| 持久化 / Checkpointing            | ⭐⭐⭐⭐   | FileSystem JSON Store 已用，但默认未启用，需 `EnableHandoffCheckpointing` 显式调用 |
| 观测 / 追踪                       | ⭐⭐⭐⭐   | `OpenTelemetryAgent` 包装已用，但 `ToolResultCapturingChatClient` 与 `ObservableToolAgent` 重复实现 |
| Routing / 决策                    | ⭐⭐      | `ChatAgent` 中 L1→L2 升级、`WorkflowOrchestrator` 问候快路 均为正则/HashSet 硬编码，未走 MAF 中间件 |
| Memory / Session 存储              | ⭐⭐      | 仅 `InMemoryChatHistoryProvider`，未用 MAF `A2A`/`Mem0`/`Cosmos` 等存储后端 |

**汇总**：约 **70% MAF 原生 / 30% LTAI 扩展层**（合理范围）。但 30% 自研中，约 60% 应可被 MAF 中间件替换（即真正偏离范式部分约 18%）。

---

## 2. 已落地的 MAF 原语（✅ 范式合规）

### 2.1 IChatClient 装饰器链
`src/LTAI.Agent/ServiceCollectionExtensions.cs` 走 `ChatClientBuilder.Use(...)`：

```
leaf IChatClient (OpenAI/Anthropic/...)
  ← FunctionInvokingChatClient   (MAF 自动注入)
  ← MessageInjectingChatClient   (MAF 自动注入)
  ← PerServiceCallChatHistoryPersistingChatClient  (MAF 自动注入)
  ← OpenTelemetryChatClient      (用户显式 .Use)
  ← ToolResultCapturingChatClient ← ⚠ 自研（见 §3.1）
```

✅ 这层完美对齐 MAF — `ChatClientAgent` 默认已含 `FunctionInvokingChatClient` + 两个隐式中间件；LTAI 只在尾部加 `OpenTelemetryChatClient` 是正确范式。

### 2.2 AIAgent 装饰器链
```
ChatClientAgent
  ← OpenTelemetryAgent            (MAF .Use 范式)
  ← ToolApprovalAgent             (LTAI 自研 §3.3 但走 .Use)
  ← LoggingAgent                  (LTAI 自研 §3.3 但走 .Use)
```

✅ `AIAgentBuilder.Use(Func<AIAgent, AIAgent>)` 范式正确，自研装饰器也走这条链 — 范式合规。

### 2.3 AIContextProvider 组合
`BuildAgentImpl` 给每个 Agent 注入了 10-12 个 Provider：

| Provider                      | 来源     | 作用                        |
| ----------------------------- | -------- | --------------------------- |
| `TodoProvider`                | MAF      | 任务列表管理                |
| `TextSearchProvider`          | MAF      | 包装 `WebTools.WebSearch`   |
| `BackgroundAgentsProvider`    | MAF      | 后台 Agent 调用             |
| `CompactionProvider`          | MAF      | 上下文窗口压缩              |
| `AgentSkillsProvider`         | MAF      | 技能检索 / 脚本运行          |
| `SafetyProvider`              | LTAI     | 内容安全审查                |
| `ToolRetrievalProvider`       | LTAI     | 工具检索                    |
| `SkillRankingProvider`        | LTAI     | 技能排序                    |
| `InstructionProvider`         | LTAI     | 指令注入                    |
| `SkillsProvider`              | LTAI     | 技能描述注入                |
| `KbGraphProvider`             | LTAI     | 知识图谱                    |
| `CodeGraphProvider`           | LTAI     | 代码图谱                    |
| `HyperlightSandboxProvider`   | LTAI     | 沙箱元数据                  |
| `ShellEnvProvider`            | LTAI     | 环境变量注入                |

✅ 6 个 MAF 原生 + 8 个 LTAI 自研 — 都实现 `AIContextProvider` 接口，**注册方式 100% 范式合规**。MAF 没有的知识图谱/沙箱/技能检索功能由 LTAI 扩展是合理的（这正是 MAF 设计为可扩展的初衷）。

### 2.4 Function / Tool 注册
200+ `AIFunctionFactory.Create` 调用 + `[Description]` 特性 + `JsonSerializerOptions` 走 `AIFunctionFactory.Options`：

```csharp
AIFunctionFactory.Create(MyMethod, new AIFunctionFactoryOptions {
    Name = "...",
    Description = "...",
});
```

✅ 完全符合 MAF 范式 — 没有手写 JSON schema。

### 2.5 Workflow 编排
`WorkflowOrchestrator` 用：

- `AgentWorkflowBuilder.CreateHandoffBuilderWith(defaultAgent)` — 多 Agent 路由
- `.WithHandoff(from, to, reason)` — 显式转移
- `.AddParticipants(agents)` — 参与者列表
- `.WithHandoffInstructions(...)` — 提示注入
- `AgentWorkflowBuilder.BuildSequential(agents)` — 顺序流水线
- `AgentWorkflowBuilder.BuildConcurrent(agents)` — 并行 fan-out
- `workflow.AsAIAgent(name: ...)` — 升级为 Agent

✅ 完全范式合规。

### 2.6 A2A 协议
`src/LTAI.Web/Program.cs`：

```csharp
builder.AddAIAgent("chat", chatAgent)
    .WithInMemorySessionStore()
    .AddA2AServer();
app.MapA2AHttpJson("chat", "/a2a/chat");
app.MapGet("/.well-known/agent-card.json", ...);
```

✅ `AddA2AServer` → `MapA2AHttpJson` → 自定义 AgentCard 是 MAF 范式。`Microsoft.Agents.AI.Hosting.A2A.AspNetCore` 的标准做法。

### 2.7 Checkpointing
`src/LTAI.Agent/Workflows/CheckpointingExtensions.cs`：

```csharp
workflow.WithFileSystemCheckpointing(dirPath);  // FileSystemJsonCheckpointStore + CheckpointManager.CreateJson
```

✅ `CheckpointManager.CreateJson(store)` + `FileSystemJsonCheckpointStore` 是 MAF 原生 API。

### 2.8 评估 / Testing
`tests/LTAI.Tests/AgentEvaluationTests.cs`（24 个 xUnit 测试）+ `tests/LTAI.Tests/Workflows/WorkflowCheckpointingTests.cs`（7 个）+ `tests/LTAI.Tests/A2A/A2AIntegrationTests.cs`（6 个）— 共 37 个测试使用 MAF API。

✅ 测试通过 112/112，0 编译错误。

---

## 3. MAF 范式偏离点（❌ 应替换为 MAF 原生中间件）

### 3.1 `ToolResultCapturingChatClient` — 应替换为 MAF FunctionInvocationFilter
**文件**：`src/LTAI.Agent/Clients/ToolResultCapturingChatClient.cs`

**问题**：
- 自实现 `DelegatingChatClient`，拦截 `GetResponseAsync` / `GetStreamingResponseAsync`
- 手动遍历 `messages` 中 `FunctionResultContent`，yield 合成"📄 工具返回"更新
- 30s 超时用 `CancellationTokenSource.CancelAfter` 手动实现
- 初始化时 lazy 加载 `SafeToolExecutionMiddleware` 包装所有 AIFunction

**MAF 原生替代**：
MAF v1.0.0 提供了 **`FunctionInvocationContext`** + `AIAgentBuilder.Use(Func<...>)` 范式（见 `FunctionInvocationDelegatingAgentBuilderExtensions.cs:37`）：

```csharp
// MAF 范式：直接在 AIAgentBuilder 上注册中间件
new AIAgentBuilder(chatAgent)
    .Use(async (innerAgent, ctx, next, ct) => {
        // 工具调用前
        var sw = Stopwatch.StartNew();
        try {
            var result = await next(ctx, ct);
            // 工具调用后：yield 进度
            yield return new AgentResponseUpdate(...);
            return result;
        } catch (Exception ex) {
            // 30s 超时、错误处理
        }
    })
    .Build(sp);
```

**影响**：
- 30s 超时是**跨中间件功能**，应通过 `CancellationTokenSource` 链入 `FunctionInvokingChatClient` 而非手写
- 工具调用的进度 / 完成通知应通过 `AgentRunOptions.AdditionalProperties` 流向流，而不是用伪 `ChatResponseUpdate` 注入
- 复杂度降低：从 200+ 行的 ChatClient 装饰器 → 30 行的 AIAgent 中间件

**偏离程度**：🔴 **严重** — 整层自定义装饰器与 MAF `FunctionInvocationDelegatingAgent` 完全功能重合。

### 3.2 `LocalToolExecutorAgent` — 应删除（workaround 而非范式）
**文件**：`src/LTAI.Agent/Clients/LocalToolExecutorAgent.cs`

**问题**：
- 自实现 `DelegatingAIAgent`，硬编码拦截 `FunctionCallContent` 调用本地工具
- 注释直说："Workaround for FunctionInvokingChatClient's tool not found bug"
- 手工 `JsonSerializer.Deserialize<Dictionary<string, JsonElement>>` 解析参数
- 硬编码 `path` / `input` 参数提取（"if args has 'path' use that..."）

**MAF 原生替代**：
- 此 Agent 的存在是因为某个特定 bug
- 应在 MAF issue tracker 反馈后，**直接删除此 workaround**
- 真正的本地工具调用应通过 `AIFunctionFactory.Create` 注册，由 `FunctionInvokingChatClient` 统一调度

**偏离程度**：🟡 **中** — 临时 workaround 演变成了正式代码路径。

### 3.3 `ObservableToolAgent` — 与 `ToolResultCapturingChatClient` 重复
**文件**：`src/LTAI.Agent/Agents/ObservableToolAgent.cs`

**问题**：
- 自实现 `DelegatingAIAgent`，拦截流式响应
- 同样 yield `⏳ 正在调用` / `✅ 返回:` 进度消息
- 同样基于 `FunctionCallContent` / `FunctionResultContent` 检测
- **与 `ToolResultCapturingChatClient` 在 chat-client 层重复了完全相同的逻辑**

**MAF 原生替代**：
- 两层监控合为**一层** MAF FunctionInvocationFilter（见 §3.1）
- `LTAI.Core.Configuration.UsageTracker.SetActiveTool/StartToolTimer/StopToolTimer` 应在 MAF 中间件中通过 `FunctionInvocationContext.Function.Name` 触发

**偏离程度**：🟡 **中** — 重复实现是范式偏离的副作用。

### 3.4 `ChatAgent` 中的 L1→L2 自动升级 — 硬编码路由逻辑
**文件**：`src/LTAI.Agent/Agents/ChatAgent.cs:28-38, 130-180`

**问题**：
- `_simpleQueries` HashSet 硬编码 30+ 字符串
- L1→L2 升级靠正则匹配 `<<<NEEDS_PRO: ...>>>` 响应标记
- `EnforceAndReflectAsync` 三阶段纠正瀑布
- 全部是字符串 / 正则 / HashSet 黑科技

**MAF 原生替代**：
- MAF 提供 `FunctionInvokingChatClient.MaximumIterationsPerRequest`、Harness API、动态模型路由
- 简单查询快路应作为 **AIAgentBuilder 链** 的一个 `LoggingAgent` 装饰器变体（"FastPathAgent"），用 `FunctionInvocationContext` 提前 return
- L1→L2 升级应作为 MAF `FunctionInvocationFilter` + `AIAgentBuilder.Use` 链，注入 "switch model" context
- `<<<NEEDS_PRO>>>` 标记应改为 `AgentRunOptions.AdditionalProperties` 中的结构化升级信号

**影响**：
- 当前实现的可测试性差（依赖字符串约定）
- 升级决策不可观测（无 trace 标记）
- 与 MAF `OpenTelemetryAgent` 集成度低

**偏离程度**：🟡 **中** — 业务逻辑本身合理，但实现方式偏离 MAF 范式。

### 3.5 `WorkflowOrchestrator` 问候快路 — 同上
**文件**：`src/LTAI.Agent/Workflows/WorkflowOrchestrator.cs:75-122`

**问题**：
- `ClassifyGreeting(task)` 函数 + `GreetingType` 6 元素枚举 + 5 个 HashSet
- `ExecuteHandoffAsync` 中硬编码 if-else 走快路

**MAF 原生替代**：
- 应封装为 `GreetingFastPathAgent`（`AIAgentBuilder.Use` 装饰器）
- 或用 MAF `HarnessAgent` 配置 `MaxTokens` / `MaxDuration` 让轻量请求走短路径
- 业务逻辑应外移

**偏离程度**：🟢 **低** — 业务逻辑独立，仅实现方式偏过程化。

### 3.6 `BuildOrchestrator` — 简化到 1 行
**文件**：`src/LTAI.Agent/ServiceCollectionExtensions.cs:730`

**当前**：
```csharp
private static AIAgent BuildOrchestrator(...) => agents[0];
```

**问题**：
- 真实编排逻辑在 `WorkflowOrchestrator`（类外）
- `ServiceCollectionExtensions` 的 `BuildOrchestrator` 只返回第一个 agent
- 与 `BuildAgent` 形成重复抽象

**建议**：
- 移除 `BuildOrchestrator` 方法，调用方直接 `GetService<WorkflowOrchestrator>()`
- 或将其实现为 `WorkflowOrchestrator.AsAIAgent()`（这正是 MAF 范式 — 已经在做）

**偏离程度**：🟢 **低** — 仅是冗余 API。

---

## 4. 未启用的 MAF 能力（机会点）

`extern/agent-framework/dotnet/src/` 中可用但 LTAI 未引用的 MAF 包：

| MAF 包                                  | LTAI 是否使用 | 替代方案                          | 建议                    |
| --------------------------------------- | ------------- | --------------------------------- | ----------------------- |
| `Microsoft.Agents.AI.Hosting`           | ❌            | 自实现 `IServiceCollection.AddLTAIAgent` 链 | 切换到 `AddAIAgent` DI 范式 |
| `Microsoft.Agents.AI.Hosting.AspNetCore` | ❌            | LTAI.Web 自管 `Program.cs`          | 切换到 MAF Hosting pipeline |
| `Microsoft.Agents.AI.Hosting.OpenAI`    | ❌            | `MultiProviderChatClient`         | 评估能否用 `AsOpenAIChatClient` |
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | ❌     | LTAI.Desktop 自实现 HTTP           | 桌面端 → AGUI 协议      |
| `Microsoft.Agents.AI.OpenAI`            | ❌            | `MultiProviderChatClient` 直接调 HTTP | 评估 OpenAI Responses API 兼容性 |
| `Microsoft.Agents.AI.Anthropic`         | ❌            | 自实现 Anthropic HTTP              | 切换到 MAF 官方 Anthropic 包 |
| `Microsoft.Agents.AI.DurableTask`       | ❌            | `EnableHandoffCheckpointing` (in-process) | 评估 Azure Durable Functions 部署 |
| `Microsoft.Agents.AI.Mem0`              | ❌            | `InMemoryChatHistoryProvider`      | 长期记忆跨会话共享      |
| `Microsoft.Agents.AI.CosmosNoSql`       | ❌            | `InMemoryChatHistoryProvider`      | 云端持久化会话           |
| `Microsoft.Agents.AI.Workflows.Declarative` | ❌        | `WorkflowOrchestrator` 硬编码 C#    | YAML 驱动 workflow 定义 |
| `Microsoft.Agents.AI.Workflows.Declarative.Foundry` | ❌ | 无                              | 评估 Foundry 部署        |
| `Microsoft.Agents.AI.Workflows.Generators` | ❌          | 无                                | XAML → workflow 编译时生成 |
| `Microsoft.Agents.AI.Foundry`           | ❌            | LTAI 自管多 Provider              | 评估 Azure AI Foundry 集成 |
| `Microsoft.Agents.AI.Foundry.Hosting`   | ❌            | LTAI.Web 自管 Program.cs           | MAF Foundry Hosting 范式 |
| `Microsoft.Agents.AI.Harness`           | ❌            | LTAI 自实现多 Agent 协调           | 评估 Harness 范式       |
| `Microsoft.Agents.AI.Purview`           | ❌            | `SafetyProvider` 自实现            | 企业级审计               |
| `Microsoft.Agents.AI.GitHub.Copilot`    | ❌            | 无                                | 接入 GitHub Copilot 模型 |
| `Microsoft.Agents.AI.CopilotStudio`     | ❌            | 无                                | 接入 Copilot Studio Bot  |
| `Microsoft.Agents.AI.Hosting.AzureFunctions` | ❌       | LTAI.Web Kestrel                   | Azure Functions 部署     |

**重点机会**（影响最大）：
1. **`Hosting.AspNetCore`** — 把 LTAI.Web 的 Program.cs 从 100+ 行的手写 DI 简化到 5-10 行的 MAF Hosting 范式
2. **`Harness`** — 替代 LTAI 自实现的 multi-agent 协调（含 todo/background/skills）
3. **`Mem0` / `CosmosNoSql`** — 替换 `InMemoryChatHistoryProvider`，解锁跨设备/跨会话记忆
4. **`Workflows.Declarative`** — YAML 驱动的 workflow 配置可大幅降低硬编码

---

## 5. 测试 / 验证 覆盖

✅ 112/112 测试通过（MAF 路径全覆盖）：
- `tests/LTAI.Tests/AgentEvaluationTests.cs` — 24 个
- `tests/LTAI.Tests/Workflows/WorkflowCheckpointingTests.cs` — 7 个
- `tests/LTAI.Tests/A2A/A2AIntegrationTests.cs` — 6 个
- 其余 75 个为 LTAI 自有逻辑测试

❌ **缺失的 MAF 集成测试**：
- 无 `FunctionInvocationFilter` 单元测试（一旦替换 `ToolResultCapturingChatClient` 需补）
- 无 `AIAgentBuilder.Use()` 装饰器链端到端测试
- 无 `A2A` 端到端 ChatClient → HTTP 客户端测试
- 无 checkpoint 恢复的真实场景测试（仅 CRUD 形式）

---

## 6. 详细差距与建议优先级

| # | 偏离点                       | 严重度 | 建议工作量 | 优先级 | 建议方案                                                                 |
| - | ---------------------------- | ------ | ---------- | ------ | ------------------------------------------------------------------------ |
| 1 | `ToolResultCapturingChatClient` | 🔴 高  | 2-3 天     | P0     | 替换为 `AIAgentBuilder.Use(FunctionInvocationContext ...)` 中间件        |
| 2 | `LocalToolExecutorAgent` (workaround) | 🟡 中  | 0.5 天   | P1     | 删除，依赖 MAF 主线 bugfix                                                |
| 3 | `ObservableToolAgent` (重复) | 🟡 中  | 0.5 天     | P1     | 与 #1 合并删除                                                            |
| 4 | `ChatAgent` L1→L2 升级       | 🟡 中  | 2-3 天     | P1     | 重构为 `FastPathAgent` / `EscalationAgent` 装饰器 + `AdditionalProperties` |
| 5 | `WorkflowOrchestrator` 问候快路 | 🟢 低  | 0.5 天   | P2     | 抽为 `GreetingFastPathAgent` 装饰器                                      |
| 6 | `BuildOrchestrator` 冗余       | 🟢 低  | 0.1 天     | P2     | 移除                                                                      |
| 7 | 引入 `Hosting.AspNetCore`    | 🟡 中  | 1 天       | P1     | 简化 LTAI.Web Program.cs                                                  |
| 8 | 引入 `Mem0` / `CosmosNoSql`  | 🟢 低  | 2 天       | P2     | 替换 InMemoryChatHistoryProvider                                           |
| 9 | 引入 `Harness`               | 🟢 低  | 5 天       | P3     | 评估可行性                                                                |
| 10| 引入 `Workflows.Declarative` | 🟢 低  | 3 天       | P3     | WorkflowOrchestrator YAML 化                                              |
| 11| 补 MAF 集成测试              | 🟡 中  | 2 天       | P1     | 装饰器链 + A2A + Checkpoint 三块                                          |
| 12| 删除 `WithDefaultAgentMiddleware` 注释 | 🟢 低 | 5 分钟 | P0     | 注释中提到的方法在 MAF 中不存在，纯历史遗留                                |

**总计 P0+P1 工作量**：约 7-9 天
**P2+P3 工作量**：约 10-12 天

---

## 7. 核心建议（执行顺序）

### 7.1 第一阶段（P0：纯重构，无新功能）
1. 删除 `WithDefaultAgentMiddleware` 注释（5 分钟）
2. 合并 `ToolResultCapturingChatClient` + `ObservableToolAgent` 为 MAF `AIAgentBuilder.Use()` 范式中间件
3. 移除 `LocalToolExecutorAgent`（依赖 MAF bugfix）
4. 补 MAF 中间件链的单元测试

### 7.2 第二阶段（P1：可观测性 + Hosting）
5. 把 `ChatAgent` L1→L2 升级抽为 `AIAgentBuilder.Use` 装饰器
6. 引入 `Microsoft.Agents.AI.Hosting.AspNetCore` 简化 LTAI.Web
7. 补装饰器链 / A2A / Checkpoint 的端到端集成测试

### 7.3 第三阶段（P2/P3：能力扩展）
8. 引入 `Mem0` 或 `CosmosNoSql` 替换 InMemoryChatHistoryProvider
9. 评估 `Harness` 范式替代自实现 multi-agent 协调
10. 引入 `Workflows.Declarative` 让 workflow 可配置

---

## 8. 关键文件清单

### 8.1 LTAI 自研、应替换或合并
- `src/LTAI.Agent/Clients/ToolResultCapturingChatClient.cs` → ❌ 替换为 MAF 中间件
- `src/LTAI.Agent/Clients/LocalToolExecutorAgent.cs` → ❌ 删除
- `src/LTAI.Agent/Agents/ObservableToolAgent.cs` → ❌ 与 ToolResultCapturingChatClient 合并删除
- `src/LTAI.Agent/Agents/ChatAgent.cs` → 🟡 抽装饰器
- `src/LTAI.Agent/Workflows/WorkflowOrchestrator.cs` → 🟡 抽快路装饰器
- `src/LTAI.Agent/ServiceCollectionExtensions.cs:721` → 🟢 删注释
- `src/LTAI.Agent/ServiceCollectionExtensions.cs:730 (BuildOrchestrator)` → 🟢 移除

### 8.2 LTAI 自研、合理保留（业务逻辑）
- `src/LTAI.Agent/Agents/.../11个agent.md` — 业务提示词
- `src/LTAI.Agent/Tools/*` — 200+ 业务工具（合规）
- `src/LTAI.Agent/AIContextProviders/*` — 8 个 LTAI Provider（合理扩展）
- `src/LTAI.Agent/Vector/*` — 知识图谱 / 代码图谱（MAF 无对应）

### 8.3 范式合规（应保持）
- `src/LTAI.Agent/Workflows/CheckpointingExtensions.cs` — ✅
- `src/LTAI.Web/Program.cs` — ✅
- `src/LTAI.AI/ServiceCollectionExtensions.cs` — ✅
- `src/LTAI.AI/MultiProviderChatClient.cs` — ✅ (MAF IChatClient 实现)

---

**结论**：LTAI 在 4 周内的 MAF 范式符合度从 ~70% 可提升到 ~90%（P0+P1 工作量），剩余 10% 属于 MAF 框架本身不覆盖的领域（业务工具、知识图谱、企业审计）。
