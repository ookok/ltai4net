# 2026-06-01 MAF 范式化迁移

## Goal
先用 Microsoft Agent Framework（MAF，`extern/agent-framework` 源码）替代自研实现（约 1000+ 行），再集成 MAF 全部功能特性（provider / hosting / workflow / integration），最终让项目走 MAF 标准范式，不再重复造轮子。

## 审查结论
- **当前 MAF 采用度：≈ 95%**（核心范式 + 协议层 + 编排层 + 集成 + 协议化全部走 MAF，仅余 WasmtimeSandbox / Skill Evolution / Tool RAG / KG / 装饰链等业务子系统自研）
- **最大自研点（已删除）**：`OpenAiHttpClient`（411 行）→ `OpenAIChatClientFactory`（5 行）；`WorkflowOrchestrator`（438 行）→ `AgentWorkflows`（180 行）+ `GreetingClassifier`（75 行）；3 个装饰器（200 行）→ MAF `LoggingAgent` / `ToolApprovalAgent` / `OpenTelemetryAgent` 装饰链
- **遗漏特性（已补齐）**：Anthropic / OpenAI / Mem0 / MCP / Hosting.* / A2A / AGUI / OpenAI Responses / ChatCompletions / Conversations 全部集成

## 计划（6 阶段，~10 周）

| 阶段 | 主题 | 周 | 行数 | 关键决策 |
|---|---|---|---|---|
| **P1.1** | OpenAI 协议层 | 1 | -411 | DeepSeek 走 `OpenAIClient` + 自定义 `Endpoint` |
| **P1.2** | Anthropic 集成 | 1 | +0 | 新增 Claude provider |
| **P2.1** | Handoff 编排 | 1 | -100 | 协议从"文本标记"升级为"function call"，一次到位 |
| **P2.2** | Sequential/Concurrent | 1 | -80 | 同上 |
| **P2.3** | 删除 WorkflowOrchestrator | 0.5 | -258 | 整个文件删除 |
| **P3** | 装饰器清理 | 1 | -200 | LocalToolExecutor / ToolResultCapturing / Observable 改用 MAF 钩子 |
| **P4** | Hosting 迁移 | 2 | -250 | `AddAIAgent` 替代 250+ 行手写 switch-case |
| **P5** | 集成增量 | 2 | -300 | MCP / Mem0 / DurableTask，按业务迫切度 |
| **P6** | 协议化 | 1 | +0 | Hosting.AspNetCore / A2A / AGUI / Foundry / Workflows.Declarative |

## 依赖关系
```
P1.1 → P3 → P4 → P5/P6
P1.2 ↗
P2.x ─┘（与 P1.1 可并行）
```

## 关键决策（已确认）
- **D1** 按 P1→P6 顺序执行
- **D2** DeepSeek 等 OpenAI-compatible 端点用官方 `OpenAIClient` + `OpenAIClientOptions.Endpoint` 覆盖，删除自研 HTTP 客户端
- **D3** handoff 协议从"文本标记"一次升级到"function call"，接受过渡期回归风险
- **D4** P5 集成按业务迫切度排序（MCP/Mem0 优先，DurableTask 视业务）
- **D5** 计划写入 AGENTS.md（本文件）
- **D6** 沙箱选型：仅用 WasmtimeSandbox，不引入 Hyperlight（Windows 部署成本 / pre-1.0 风险权衡）
- **D7** Mem0 备选：本地 `EmbeddedMemoryProvider`（SQLite + embedding cosine）作为无 key 时的回退
- **D8** DurableTask 替代：自定义 in-process `Channel<T>` 队列，MAF DurableTask（Azure Functions）推迟到 P7
- **D9** MCP 服务端只暴露只读工具（read_file/search/glob/regex_test）—— 不暴露 shell / file write / git push 等
- **D10** A2A server 注册用 MAF 工厂重载（`AddA2AServer(services, agentName)`）延迟 agent 解析；不预先 `BuildServiceProvider` 解析 18 个 agent
- **D11** `ShellEnvironmentProvider` 在 LTAI 链路中**完全移除**（MAF 默认走 `LocalShellExecutor` → 启动持久化 PowerShell 进程，在 Windows .NET 10 preview 60+ 秒卡死）；LTAI 已有自己的 `EnvironmentProvider` + `SafeShellTool` + `WasmtimeSandbox` 覆盖
- **D12** Swashbuckle 8.x/9.x 在 .NET 10 preview 上 `TypeLoadException: GetSwagger has no implementation`（Microsoft.OpenApi 2.x ABI 兼容问题）；LTAI.Web 改用内置 `AddOpenApi()` + `MapOpenApi()`，移除 Swashbuckle 包
- **D13** 不注册全局 `/v1/responses` 和 `/v1/chat/completions` 默认端点（与 per-agent `/v1/agents/LTAI-Chat/responses` 产生 endpoint-name 冲突）；客户端用 per-agent 路由作为 default
- **D14** MAF DevUI 仅在 `IsDevelopment()` 注册并暴露（暴露 system prompt、tool 列表、模型 ID 等元数据，生产暴露有风险）
- **D15** OTel 默认 console exporter（无外部依赖）；OTLP exporter 仅在 `LTAI:Telemetry:OtlpEndpoint` 配置时激活（避免无 OTLP collector 时启动报错）

## Files to touch（实际）
### P1.1 ✅
- `src/LTAI.AI/MultiProviderChatClient.cs`（删除 OpenAiHttpClient 411 行，新增 OpenAIChatClientFactory 5 行；token 跟踪上移到路由器）
- `src/LTAI.AI/ServiceCollectionExtensions.cs`（移除 httpClient 注入，调用 OpenAIChatClientFactory.Create）
- `src/LTAI.Agent/LTAI.Agent.csproj`（添加 Microsoft.Agents.AI.OpenAI ProjectReference + System.ClientModel 1.12.0）
- `src/LTAI.AI/LTAI.AI.csproj`（同上）
- 调用点 5 处：`LTAI.Agent/ServiceCollectionExtensions.cs:543`、`LTAI.TUI/SlashCommands.cs`（3 处）、`LTAI.TUI/LLMConfigPanel.cs:134`、`LTAI.Desktop/MainWindow.cs:318`

### P1.2 ✅
- `src/LTAI.AI/MultiProviderChatClient.cs`（新增 AnthropicChatClientFactory 8 行）
- `src/LTAI.AI/LTAI.AI.csproj`（添加 Microsoft.Agents.AI.Anthropic ProjectReference）
- `src/LTAI.Core/Configuration/LTAIOptions.cs`（添加 ANTHROPIC_API_KEY 到 KnownKeys.All；`isAnthropic = name == "Anthropic"` 分支路由）

### P2 ✅（P2.1 + P2.2 + P2.3 一次完成，**用户接受回归**）
- 删除 `src/LTAI.Agent/Workflows/WorkflowOrchestrator.cs`（438 行全删）
- 新增 `src/LTAI.Agent/Workflows/AgentWorkflows.cs`（~180 行，封装 MAF 三个 WorkflowBuilder）
- 新增 `src/LTAI.Agent/Workflows/GreetingClassifier.cs`（~75 行，保留问候快速通道）
- `src/LTAI.Agent/Agents/ChatAgent.cs`（4 行：构造参数 + 2 处调用点改名）
- `src/LTAI.Agent/Tools/WorkflowTools.cs`（3 行：_wf 类型 + 2 处调用点改名）
- `src/LTAI.Agent/ServiceCollectionExtensions.cs`（3 行：DI 注册改名 + ChatAgent 注入改名）
- 净变化：**-200 行**（-438 + 180 + 75 + 10 = -173，加上原有变更约 -200）
- **回归接受**：circuit breaker、retry+fallback、并发节流（max=2）、JSON/文本 handoff 标记协议全部删除；greeting 快速通道 + 向量 top-K 选择保留
- 协议升级 D3：handoff 标记从文本/JSON 改为 function call（MAF `HandoffWorkflowBuilder` 内部用 `handoff_to_<agent_id>` 工具调用）

### P3 ✅（**用户接受回归**）
- 删除 `src/LTAI.Agent/Clients/LocalToolExecutorAgent.cs`（死代码 75 行）
- 删除 `src/LTAI.Agent/Agents/ObservableToolAgent.cs`（死代码 89 行）
- 删除 `src/LTAI.Agent/Clients/ToolResultCapturingChatClient.cs`（死代码 113 行）
- 删除 `src/LTAI.Agent/Tools/SafeToolExecutionMiddleware.cs`（死代码 26 行）
- 删除 `src/LTAI.Agent/Tools/ToolCallRepairer.cs`（死代码 218 行）
- `src/LTAI.Agent/Agents/ChatAgent.cs` `ChatStreamingAsync` 中观察 `update.Contents` 注入 UX 通知（+19 行）
- `src/LTAI.Agent/ServiceCollectionExtensions.cs`（-5 行：移除死变量 + 2 个构造参数）
- `src/LTAI.Agent/Tools/SkillRankingProvider.cs`（-4 行：移除未使用字段）
- 净变化：**-511 行**
- 回归接受：5 个死代码装饰器全部删除；tool 执行结果改由 `ChatAgent` 流观察 `FunctionCallContent`/`FunctionResultContent` 注入（MAF `FunctionInvokingChatClient` 自动接入由 `ChatClientAgent.WithDefaultAgentMiddleware` 完成）

### P4 ✅
- `src/LTAI.Agent/LTAI.Agent.csproj`（添加 Microsoft.Agents.AI.Hosting ProjectReference）
- `src/LTAI.Agent/ServiceCollectionExtensions.cs`：
  - `AddLTAIAgent` Step 1：`Dictionary<string, AIAgent>` 单例 → 循环调用 `services.AddAIAgent(name, factory, lifetime)`（MAF `IHostedAgentBuilder` keyed services）
  - 新增 `AgentDef` 记录 + `GetAgentDefinitions()` 方法（声明式 agent 配置）
  - 删除 `BuildAllAgents`（40 行）+ `BuildOrchestrator`（7 行）
  - Step 3/3d：`GetRequiredService<Dictionary<string, AIAgent>>` → `GetKeyedServices<AIAgent>(KeyedService.AnyKey)`
- 文件从 705 行 → 650 行（-55 行）
- 业务逻辑保留：`BuildAgentImpl` 不变（80+ 工具 / AIContextProviders / Plan Mode / decorators）
- 收益：agent 现在是 MAF 标准 keyed services，可被 `MapAIAgent` / MAF DevUI / 未来 P6 协议化直接发现

### P5 ✅（用户接受回归 / 自定义实现）
- **P5.0 沙箱选型**：用户决定**仅用 WasmtimeSandbox**，不引入 Hyperlight。理由：Windows 需 Hyper-V/WSL2；Hyperlight 0.x pre-1.0 风险高；Wasmtime 启动更快。
  - `src/LTAI.Agent/LTAI.Agent.csproj`：删除 `Microsoft.Agents.AI.Hyperlight` ProjectReference
- **P5.1 Mem0 / EmbeddedMemoryProvider**（~200 行新）：优先用 MAF `Mem0Provider`（远程，需 `MEM0_API_KEY`），否则用本地 `EmbeddedMemoryProvider`（SQLite + embedding cosine top-K）
  - `src/LTAI.Agent/Memory/EmbeddedMemoryProvider.cs`（新增）：SQLite `memories` 表 + BLOB embedding + cosine 相似度 top-K
  - `src/LTAI.Agent/Memory/MemoryProviderSelector.cs`（新增）：自动选择 Mem0（远程）或本地实现
  - `src/LTAI.Core/Configuration/LTAIOptions.cs`：`MEM0_API_KEY` 加入 `KnownKeys.All`
  - `src/LTAI.Agent/ServiceCollectionExtensions.cs`：插入到 `AIContextProviders` 链中（wasmtimeSandbox 之后、InstructionProvider 之前）
  - `src/LTAI.Agent/LTAI.Agent.csproj`：添加 `Microsoft.Agents.AI.Mem0` ProjectReference
- **P5.2 MCP 客户端**（~120 行新）：用 MAF `McpClientTaskExtensions` 接入外部 MCP server（filesystem/github 等）
  - `src/LTAI.Agent/Mcp/McpClientFactory.cs`（新增）：DI 单例，懒加载 + 缓存，stdio transport
  - `src/LTAI.Core/Configuration/LTAIOptions.cs`：`McpConfig` + `McpServerConfig` 类型；`LTAIOptions.Mcp` 属性
  - `src/LTAI.Agent/ServiceCollectionExtensions.cs`：DI 注册 + `BuildAgentImpl` 中追加 MCP 工具（plan mode 排除）
  - `src/LTAI.Agent/LTAI.Agent.csproj`：添加 `Microsoft.Agents.AI.Mcp` ProjectReference
  - `BuildAgentImpl` 改为 `async Task<AIAgent>`，调用点用 `GetAwaiter().GetResult()`（一次性启动开销）
- **P5.3 MCP 服务端**（~80 行新）：在 LTAI.Cli 暴露只读工具给外部 IDE
  - `src/LTAI.Cli/McpServer.cs`（新增）：`ltai mcp-server` 子命令，stdio transport，仅暴露 8 个 read-only 工具（read_file/list_files/glob/directory_tree/file_info/search_content/search_files/regex_test）
  - `src/LTAI.Cli/Program.cs`：dispatch + help 加 `mcp-server` 命令
- **P5.4 Channel<T> 任务队列**（~150 行新）：`Tasks/TaskQueue.cs` — in-process producer/consumer，MAF DurableTask 推迟到 P7
  - `TaskItem` record（状态：Pending/Running/Completed/Failed/Cancelled）
  - `ITaskStore` 接口 + `InMemoryTaskStore` 默认实现（可扩展 SQLite 持久化）
  - `TaskQueue` 类：Channel< TaskItem> + N 个 consumer 协程 + 状态追踪 + 事件回调
  - `EnqueueAsync(name, work, description)` 提交；`WaitAsync(id, timeout)` 阻塞等待；`List()` / `Get(id)` 查询
  - DI 单例注册

### P6 ✅（与 P5 同期完成）
- `src/LTAI.Web/Program.cs`（添加 A2A / AGUI / OpenAI Responses 端点）
- 详见 P6 决策 D10-D13

### P7 ✅
- **P7.1 MAF DevUI**（高价值，已完成）：`Microsoft.Agents.AI.DevUI` ProjectReference 添加到 LTAI.Web.csproj；`Program.cs` 中 `if (builder.Environment.IsDevelopment()) { builder.AddDevUI(); }` + `if (app.Environment.IsDevelopment()) { app.MapDevUI(); }`；验证 `/v1/entities` 返回 18 agents 全列、`/devui` 返回 MAF DevUI HTML 页面（loopback-only by default）
- **P7.2 OpenTelemetry exporters**（中价值，已完成）：LTAI.Core 已配 `AddOpenTelemetry().WithTracing/Metrics` 但**无 exporter**；LTAI.Web/Program.cs 补加：Development 环境 `AddConsoleExporter()` + 配置 `LTAI:Telemetry:OtlpEndpoint` 时 `AddOtlpExporter(opt => opt.Endpoint = new Uri(endpoint))`
- **P7.3（评估）Workflows.Declarative**：当前 `AgentWorkflows.cs`（180 行）是 C# 实现；MAF YAML workflow 可以声明式定义 agent 编排
  - **风险**：YAML DSL 学习曲线，且 LTAI 有 GreetingClassifier 等自研逻辑
  - **决策**：暂不做，等业务有需求再评估
- **P7.4（跳过/推迟）**：
  - **Foundry / Foundry.Hosting / AzureAI.Persistent / CopilotStudio** — Azure 订阅依赖
  - **Hosting.AzureFunctions / DurableTask** — D8 已推迟
  - **CosmosNoSql / Purview** — 企业合规
  - **GitHub.Copilot** — GitHub 专用
  - **Aspire DevUI hosting** — Aspire 是 dev-only 编排层，DevUI 本身已够用
- **P7.5 Workflows.Declarative**（部分工作流，已完成）：`Microsoft.Agents.AI.Workflows.Declarative` ProjectReference 添加到 LTAI.Agent.csproj；写 `ltai/workflows/greeting.yaml` 替换 `GreetingClassifier.cs`（75 行删除）；新增 `YAMLWorkflowHost.cs` 包装；5 类问候（greeting/thanks/farewell/probing/test），每个用 `ConditionGroup` + `StartsWith` PowerFx 表达式
  - **范围**：仅 greeting 快速通道（80% LTAI 流量入口）；保留 Sequential/Concurrent 的 C# 实现
  - **关键 bug 修复**：`AgentResponseUpdate` 在 `Microsoft.Agents.AI` 命名空间（不在 `Microsoft.Extensions.AI`）；`protected static new` 在 `sealed` 类中非法 → 删除
  - **收益**：用户可改 YAML 添加问候模式（无需重编译）
- **P7.6 Harness 集成**（中价值，已完成）：`Microsoft.Agents.AI.Harness` ProjectReference 添加到 LTAI.Agent.csproj；`BuildAgentImpl` 中用 `llm.AsHarnessAgent(maxCtx=64000, maxOut=opts.AI.MaxTokens, options)` 替代 `ChatClientAgent` + 3 个装饰器（`LoggingAgent` 保留为最外层）
  - **禁用** LTAI 不需要的默认 providers：`DisableFileMemory` / `DisableFileAccess` / `DisableWebSearch` / `DisableTodoProvider` / `DisableAgentModeProvider` / `DisableAgentSkillsProvider` = true
  - **保留** HarnessAgent 默认 `OpenTelemetryAgent` + `ToolApprovalAgent`（MAF 自动加，per-agent `OpenTelemetrySourceName = $"LTAI.{name}"`）
  - **保留** 9 个 LTAI 自研 `AIContextProvider`（ToolRetrievalProvider / SkillRankingProvider / [Safety] / compaction / kbGraph / codeGraph / wasmtimeSandbox / memoryProvider / InstructionProvider / EnvironmentProvider / skillsProvider）
  - **保留** `ChatHistoryProvider = new InMemoryChatHistoryProvider()`
  - **删除**：`EnableMessageInjection = true` + `RequirePerServiceCallChatHistoryPersistence = true`（HarnessAgent 默认开）
  - **删除**：`ToolApprovalAgent` + `OpenTelemetryAgent` 显式装饰（HarnessAgent 默认加）
  - **代价**：`HarnessAgent` 标 `[Experimental(DiagnosticIds.Experiments.AgentsAIExperiments)]`（已有 `#pragma warning disable MAAI001` 兜底）
  - **代价**：`compaction` 双跑：HarnessAgent 自己的 `ContextWindowCompactionStrategy(64000)` 在 chat client middleware 层跑一次 → 阈值 64000 触发；LTAI 的 `PipelineCompactionStrategy(64000)` 在 `AIContextProviders` 层再跑一次 → 已 ≤64000 不触发；`VerifiedSummarizationStrategy` 被绕过（已知回归）
  - **构建**：LTAI.Agent + LTAI.Web + LTAI.Desktop + LTAI.Cli 全绿（0 errors）
- **P7.7 决策树路由**（已完成）：`src/LTAI.Agent/Workflows/DecisionTreeRouter.cs`（~160 行新建）
  - **Stage 1**：embedding top-K by cosine similarity (default K=3) — `AgentRegistry.SelectTopKWithScoresAsync` 新增 (returns `IReadOnlyList<(string Name, float Score)>`)
  - **Stage 2**：confidence margin = top-1 score − top-2 score
  - **Stage 3**：branch — `ConfidentTopK` (margin ≥ 0.15 AND top-1 score ≥ 0.30) → use top-K；否则 `AmbiguousFallback` → 退回所有 specialists
  - **可调**：`DecisionTreeRouterOptions { TopK=3, ConfidenceMarginThreshold=0.15f, MinTopScoreThreshold=0.30f }`
  - **可观测**：`ILogger.LogInformation` 记录每个分支 (`Router: CONFIDENT margin=X` / `Router: AMBIGUOUS (margin=...)`) → P7.2 OTel exporter 自动收集
  - **兜底**：无 embedder → `NoEmbedder` 分支（用所有 specialists）；top-K 空 → `EmbeddingFailed` 分支
  - **集成**：`ServiceCollectionExtensions` Step 3 DI 注册 `DecisionTreeRouter` 单例；`AgentWorkflows` 构造参数从 `EmbeddingClient?` 改成 `DecisionTreeRouter`
  - **同步改进**：`AgentRegistry.ParseJsonArray` 重写，兼容 `["a","b"]` (JSON) 和 `[a, b]` (CSV) 两种格式 — 老 .agent.md 副本用 CSV 也能解析
- **P7.8 增量：agents CLI + greeting 扩展**（已完成，用户后续要求）
  - **`ltai agents list` / `ltai agents show <name>`** — 列出 10 agents（`LTAI-Chat`、`LTAI-Chat-Pro`、`LTAI-Code`、`LTAI-Data`、`LTAI-Frontend`、`LTAI-LLM`、`LTAI-Math`、`LTAI-System`、`LTAI-Writer`、`sql-agent`）的模型/温度/工具数/权限 (RWLX 颜色) / 描述；`show` 展示完整 system prompt + 工具表 + 权限表
  - **`ltai/cli/Program.cs`** 加 `agents` 命令 + help 两行（list + show）
  - **greeting.yaml 扩展**：英文 (hi/hello/hey/howdy/good morning/thx/cya)、中文 (原有)、i18n (hola/bonjour/ciao/こんにちは/안녕)、affirmation (ok/k/好的/嗯/yes)、garbage fallback (Length<3 或 asdf/qwerty → "我没太明白")；非垃圾 + 非问候 → 不发 SendActivity → 走 LLM handoff
- **P7 文件实际变更**：
  - `src/LTAI.Web/LTAI.Web.csproj`（添加 `Microsoft.Agents.AI.DevUI` ProjectReference）
  - `src/LTAI.Web/Program.cs`（`using Microsoft.Agents.AI.DevUI;` + `using OpenTelemetry.Trace;` + DevUI/OTel 配置）
  - `src/LTAI.Agent/LTAI.Agent.csproj`（添加 `Microsoft.Agents.AI.Workflows.Declarative` + `Microsoft.Agents.AI.Harness` ProjectReference）
  - `src/LTAI.Agent/Workflows/ltai-workflows/greeting.yaml`（新建 P7.5 + 扩展 P7.8）
  - `src/LTAI.Agent/Workflows/YAMLWorkflowHost.cs`（新建 P7.5）
  - `src/LTAI.Agent/Workflows/GreetingClassifier.cs`（**已删除** P7.5，-75 行）
  - `src/LTAI.Agent/Workflows/DecisionTreeRouter.cs`（新建 P7.7，~160 行）
  - `src/LTAI.Agent/AgentRegistry.cs`（`SelectTopKWithScoresAsync` 新增 + `ParseJsonArray` 兼容 CSV 格式）
  - `src/LTAI.Agent/Workflows/AgentWorkflows.cs`（P7.6 Harness refactor + P7.7 接入 DecisionTreeRouter）
  - `src/LTAI.Agent/ServiceCollectionExtensions.cs`（P7.6 Harness refactor + P7.7 DI 注册）
  - `src/LTAI.Cli/Program.cs`（`agents list/show` 子命令）

## 验证（每个阶段）
- `dotnet build src/LTAI.AI` / `dotnet build src/LTAI.Agent` / `dotnet build src/LTAI.Web` / `dotnet build src/LTAI.Desktop`
- `dotnet test tests/LTAI.Tests`（如有）
- 真实 LLM 调用 smoke test（DeepSeek + 至少 1 次端到端）
- 80+ 工具的 LLM 调用回归

---

# 2026-05-31 Session Persistence Improvements

## Goal
Connect SessionManager → ChatView and add SessionStatsPanel to MainWindow sidebar so users can save/load/switch sessions from the Desktop UI.

## Plan

### Steps
1. **SessionManager.cs** — add SaveSession() overload (no params, saves to _currentSession)
2. **ChatView.cs** — add _sessionManager field, constructor param, save after each response, /new calls NewSession(), expose LoadSession(string) + SessionManager property  
3. **MainWindow.cs** — create shared SessionManager, add SessionStatsPanel to sidebar, wire SessionSelected/NewSessionClicked to ChatView, refresh stats via timer

### Key Decisions
- SessionManager 在 MainWindow 中创建并注入 ChatView（而非各自独立），确保单例共享
- SessionStatsPanel 使用计时器轮询刷新而非事件通知，避免 ChatView/SessionManager 之间增加事件耦合
- NuGet 不变更 — 三个文件同在 LTAI.Desktop 项目，共享现有引用

### Files touched
- src/LTAI.Desktop/SessionManager.cs
- src/LTAI.Desktop/ChatView.cs
- src/LTAI.Desktop/MainWindow.cs

## Verification
- [ ] 构建通过：`dotnet build src/LTAI.Desktop`
- [ ] 现有测试通过：`dotnet test tests/LTAI.Tests`
- [ ] 手动验证：启动桌面端，新建会话、发送消息、关闭重开后会话列表显示历史
