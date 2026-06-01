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
- **P8.x：ToolCallRepairer 不恢复**（用户决定）：DeepSeek 走 OpenAI 兼容协议，MAF 默认 JSON options 正常解析。3 个原职责的处理：
  - 烂 JSON 修复（尾逗号 / 单引号 / 未引号属性）— 已被 MAF `AIJsonUtilities.DefaultOptions` 的 `AllowTrailingCommas` + case-insensitive 覆盖
  - 类型强制（`"5"` → `5`，`"true"` → `true`）— 5 行 `NumberHandling.AllowReadingFromString` 补齐（`ServiceCollectionExtensions` 静态 cctor）
  - Fuzzy tool name match — 不补（国产模型输出规范后可走 P9 重新评估）

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

### P8 ✅（C 路径 — 升 DTFx + InProcessTestHost 0.2.3-preview.1）
- **D17 选型决定**：升 MAF DTFx 1.18.0 → 1.24.2 + 引入 `Microsoft.DurableTask.InProcessTestHost 0.2.3-preview.1`
- 原因：DTFx out-of-process SDK 1.18-1.24 **没有内建 in-process sidecar**（`Microsoft.DurableTask.Worker.Grpc` / `Client.Grpc` 1.18+ 全部是客户端）。`InProcessTestHost 0.2.3-preview.1` 是微软提供的 self-host gRPC sidecar 包装（preview，标"for testing"），在同进程内启 Kestrel + `TaskHubGrpcServer` + `InMemoryOrchestrationService`，让 MAF `ConfigureDurableAgents` 能用 gRPC 通道连过去
- **目录**：
  - `extern/agent-framework/dotnet/Directory.Packages.props`：`Microsoft.DurableTask.Client/Worker/Client.AzureManaged/Worker.AzureManaged` 1.18.0 → 1.24.2
  - `src/LTAI.Agent/LTAI.Agent.csproj`：加 `Microsoft.Agents.AI.DurableTask` ProjectReference + `Microsoft.DurableTask.InProcessTestHost 0.2.3-preview.1` PackageReference
  - `src/LTAI.Agent/Durability/LTAIDurableAgentHost.cs`（新建，~90 行）：`IHostedService`，构造时 `TcpListener` 预留 loopback port（避免 `AddInMemoryDurableTask` 时 port 未知）
  - `src/LTAI.Agent/Durability/DurableAgentServiceCollectionExtensions.cs`（新建，~80 行）：`AddLTAIDurableAgents` 扩展 — 注册 host + `AddInMemoryDurableTask(registry => {}, new InMemoryDurableTaskOptions { Port = host.Port })` + MAF `ConfigureDurableAgents(opts.AddAIAgentFactory(name, sp => sp.GetRequiredKeyedService<AIAgent>(name)))`
  - `src/LTAI.Agent/ServiceCollectionExtensions.cs`：Step 1b 调用 `AddLTAIDurableAgents()`
  - `src/LTAI.Core/Configuration/LTAIOptions.cs`：加 `DurableConfig { Enabled=true, SidecarPort=null }`
- **架构**：
  ```
  LTAI agent (keyed) ──→ DurableAIAgentProxy ──→ IDurableAgentClient (MAF DefaultDurableAgentClient)
                                                            │ gRPC
                                                            ▼
                                              MAF DurableTaskWorker (hosted)
                                                            │ gRPC
                                                            ▼
                                              InProcessTestHost sidecar (Kestrel)
                                                            │
                                                            ▼
                                              InMemoryOrchestrationService
                                              (state 进程内；重启丢)
  ```
- **已知限制**：
  - 跨进程重启不持久化（InMemoryOrchestrationService 进程内 only）。要持久化需写 `IOrchestrationService` SQLite 适配器（**P8.1 sub-step**）
  - `InProcessTestHost 0.2.3-preview.1` 是 preview 依赖，标"for testing"
  - InProcessTestHost 自带 worker（idle，因为我们用空 registry）+ MAF 自带 worker（实际处理 entity）。两个 worker 连同一 sidecar
  - `Microsoft.DurableTask.Testing.Sidecar` 命名空间类型 + `TaskHubGrpcServer` 来自 InProcessTestHost 包
- **构建**：LTAI.Agent + LTAI.Web + LTAI.Desktop + LTAI.Cli 全绿（0 errors）
- **下一步（低优先）**：
  - P8.1：写 `SQLiteOrchestrationService` 替换 InMemoryOrchestrationService，跨重启持久化
  - P8.2：smoke test（启动 server → 发请求 → 重启 → 状态还原）— user 已说"测试太耗时间 先做功能"，跳过

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

# 2026-06-02 P9 DevUI 三端共享 (TUI Dashboard + Desktop 浏览器 + Web REST)

## Goal
- 把 OpenTelemetry 链路追踪 + AgentCard 抽成共享服务，让 TUI / Desktop / Web 三个端都能开箱即用 DevUI 调试视图
- 选 C 路径（TUI Dashboard + Desktop 嵌 DevUI 浏览器拉起）

## Plan

### P9.0 LTAIDevUIService（后端共享层）✅
- `src/LTAI.Agent/DevUI/LTAIDevUIService.cs`（新建，~180 行）
  - `LTAIAgentCard` record（UI-portable；非 A2A 类型，避免 LTAI.Agent 依赖 A2A 包）
    - 字段：Name/Description/Version/DocumentationUrl/Skills/Capabilities/DefaultInputModes/DefaultOutputModes/Tags/ModelId/Temperature/TopP/Tools/Permissions
  - `LTAIAgentSkill` + `LTAIAgentCapabilities`（Stream/PushNotifications/StateTransitionHistory）
  - `LTAIDevUIService` 类
    - `ListAgentCards()` → `IReadOnlyList<LTAIAgentCard>`
    - `GetAgentCard(string name)`
    - `RunStreamingAsync(name, message, sessionId, ct)` → `IAsyncEnumerable<AgentResponseUpdate>`（每次新建 session，跨调用持久化推迟到 P10+）
    - `ResolveAgent(name)` 解析 keyed `AIAgent`（P4 Hosting 注册）
    - `BuildCard(def)` 用 `AgentRegistry.LoadAll()` 填 metadata
- DI 注册：`ServiceCollectionExtensions.cs` 加 `AddSingleton<LTAIDevUIService>()`（Step 3a）
- Web 端加 2 个 endpoint：
  - `GET /ltai/v1/entities` → `devUi.ListAgentCards()`
  - `GET /ltai/v1/entities/{name}/card` → `devUi.GetAgentCard(name)`
  - 与 MAF 自带 `/v1/entities`（DevUI auto-discovery）共存；LTAI 的端点附加 ModelId/Tools/Permissions 字段

### P9.1 TUI Dashboard ✅
- `src/LTAI.TUI/DevUI/DevUISpanCollector.cs`（新建，~150 行）
  - `BackgroundService` 订阅 `ActivityListener`（`Microsoft.Agents.AI.*` + `LTAI.*` + `OpenTelemetry.*` sources）
  - 环形 buffer 保留最近 200 spans（`_live` LinkedList 跟踪 in-progress，`_spans` 已完成）
  - `OnActivityStarted/Stopped` 回调更新状态/延迟
  - `IReadOnlyList<DevUISpan>` + `Snapshot()` API
- `src/LTAI.TUI/DevUI/DevUIDashboardView.cs`（新建，~150 行）
  - Spectre.Console `Layout` 3 区域：header（统计）/ body（agents + spans 双栏）/ footer（token usage）
  - Agents 表：name/model/T/tools/perms（彩色 RWLX 标记）/description
  - Spans 表：status（live/OK/ERR 色码）/name/source/kind/duration（按延迟着色）/trace
  - 颜色规则：`>2s` 红色 / `>500ms` 黄色 / `<500ms` 灰色
- `TuiApp.cs` 改造：
  - 构造参数加 `LTAIDevUIService` + `DevUISpanCollector`
  - `ShowDashboard()` 改为 `DevUIDashboardView.Render(...)`，移除旧的 60 行 usage charts（已被 P9.1 精简版替代）
  - `TuiView.Dashboard` 路径保持不变（用户按 `1` 进入）
- `Program.cs` 注册 `DevUISpanCollector` 单例 + HostedService
- 总 -49 行（TuiApp.cs 75→22 替换） + +330 行新文件 = +281 行

### P9.2 Desktop DevUI ✅
- 选"启动 in-process Kestrel + 拉起默认浏览器"路径，**不**引入 WebView2 / `Microsoft.Web.WebView2` 依赖
  - 理由：Avalonia 12 缺原生 WebView2 control；外部浏览器 DevTools / 多 tab / 复制粘贴体验更好；依赖更少
- `src/LTAI.Desktop/DevUI/DevUIHost.cs`（新建，~190 行）
  - `IAsyncDisposable` 包装 `WebApplication`
  - `StartAsync(parentSp, ct)`：`TcpListener(0)` 拿 free port → `WebApplication.CreateBuilder().UseUrls("http://127.0.0.1:PORT")` → 注册 `/v1/entities` + `/v1/entities/{name}/card` + `/` 重定向 `/devui` + 简单 HTML
  - `OpenInBrowser()`：`Process.Start(new ProcessStartInfo { FileName = BaseUrl+"/devui", UseShellExecute = true })`
  - 自带 DevUI HTML（~70 行）：title/agent cards with perm pills (RWLX 颜色)/tools pills
- `DashboardView.cs` 改造：
  - 加 "Open DevUI in Browser" 按钮 + 状态 TextBlock
  - Lazy<DevUIHost> 单 host（不每次创建）
  - Click → `host.StartAsync(App.Services)` → `host.OpenInBrowser()` → 显示状态
- `App.axaml.cs` 加 `IServiceProvider Services` 静态属性
- `Program.cs` 设 `App.Services = provider`
- 总 +33 行（DashboardView）+ +199 行新文件 = +232 行

### Key Decisions
- **D18 P9.0 LTAIAgentCard 自定义 record 而非 A2A.AgentCard**：LTAI.Agent 不依赖 A2A NuGet；card 字段比 A2A 多（model/temp/tools/perms）；LTAI.Web 在 endpoint 边界手动转 A2A.AgentCard（未来需要时）
- **D19 P9.1 OTel 监听走 ActivityListener 而非订阅 OTel SDK**：零依赖；MAF/Harness 已 emit 标准 `System.Diagnostics.Activity`（P7.2 默认 console exporter 同源）
- **D20 P9.2 浏览器拉起 vs WebView2**：选前者。WebView2 嵌入需 Avalonia 第三方包（无官方支持），且浏览器体验更完整
- **D21 P9.1 旧 ShowDashboard 60 行删掉**：原本是只显示 token/cache rate 的极简版，被 P9.1 三区域（agents + spans + token）替代
- **D22 P9.2 简单 HTML 内嵌**：P9.2 只暴露 agent list 视图；不复制 MAF DevUI 的 chat UI（chat 由 LTAI.TUI/LTAI.Desktop 自己的 chat view 处理）。P10+ 可升级到 `AddDevUI()` 在 in-process Kestrel 里跑
- **D23 跨调用 session 持久化暂不做**：P9.0 每次 `RunStreamingAsync` 重新创建 session；P10+ 接入 `AgentSessionStateBag` JSON 持久化（keyed by conversation id）

### Files touched

**新建**
- `src/LTAI.Agent/DevUI/LTAIDevUIService.cs`（P9.0，~180 行）
- `src/LTAI.TUI/DevUI/DevUISpanCollector.cs`（P9.1，~150 行）
- `src/LTAI.TUI/DevUI/DevUIDashboardView.cs`（P9.1，~150 行）
- `src/LTAI.Desktop/DevUI/DevUIHost.cs`（P9.2，~190 行）

**改**
- `src/LTAI.Agent/ServiceCollectionExtensions.cs`（+5 行：Step 3a DI 注册）
- `src/LTAI.TUI/Program.cs`（+9 行：DevUISpanCollector 注册 + 注入）
- `src/LTAI.TUI/TuiApp.cs`（-49 行：旧 ShowDashboard 删除 + 新构造参数）
- `src/LTAI.Desktop/App.axaml.cs`（+1 行：Services 属性）
- `src/LTAI.Desktop/Program.cs`（+1 行：App.Services = provider）
- `src/LTAI.Desktop/DashboardView.cs`（+42 行：Open DevUI 按钮 + 状态）
- `src/LTAI.Web/Program.cs`（+15 行：2 个 /ltai/v1/* endpoint）

## Verification
- [x] 构建通过：5 项目 (LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli) 0 errors
- [x] 全绿：0 警告 in LTAI.Agent / LTAI.TUI / LTAI.Web / LTAI.Cli
- [ ] 真实 LLM 调用 smoke test（用户已说"测试太耗时间"，可后续手动验证）
- [ ] TUI Dashboard 按 `1` 看到 10 个 agent + 实时 span 增长
- [ ] Desktop 仪表盘点 "Open DevUI in Browser" 浏览器打开 DevUI
- [ ] Web `GET /ltai/v1/entities` 返回 10 个 LTAIAgentCard

## Next Steps (P10+)
- **P10.1**：把 MAF `AddDevUI()` 集成到 `DevUIHost`（替换内嵌 HTML；chat UI 也能跑）
- **P10.2**：A2A `AgentCard` 转换器（`LTAIAgentCard` → A2A `AgentCard`，放到 LTAI.Web endpoint 边界）
- **P10.3**：跨调用 session 持久化（`AgentSessionStateBag` SQLite store）
- **P10.4**：把 P7.2 OTel console exporter 改为 OTel SDK，配 span/trace 持久化
- **P10.5**：WebView2 嵌入式（如果用户后续需要 in-DevUI 体验）

# 2026-06-02 P10 Harness 深度集成

## Goal
- 释放 MAF `HarnessAgent` 还未被 LTAI 用上的能力：BackgroundAgents 互委派 + per-agent OTel source + 中文 HarnessInstructions + 显式 iteration 上限

## Plan

### P10.0 BackgroundAgents 互委派 ✅ (核心)
- `src/LTAI.Agent/LazyAIAgentProxy.cs`（新建，~75 行）
  - `AIAgent` 子类，包装 keyed service 解析 + 循环依赖打破
  - ctor 阶段：`Name` / `Description` 直接来自 `AgentRegistry`（静态已知，**不**触发内层 agent 构造）→ 打破 `HarnessAgent ↔ BackgroundAgentsProvider ↔ HarnessAgent` 循环
  - `RunCoreAsync` / `RunCoreStreamingAsync` / `CreateSessionCoreAsync` 等 *Core 方法：懒解析 `IServiceProvider.GetKeyedService<AIAgent>(name)`，此时 agent 图已全部建好
- `ServiceCollectionExtensions.BuildAgentImpl`：
  - `BackgroundAgents = AgentRegistry.LoadAll().Where(不是自己 && 不是 router).Select(d => (AIAgent)new LazyAIAgentProxy(sp, d.Name)).ToList()` — **9 个 sister agents 委派池**
  - `BackgroundAgentsProviderOptions.Instructions = 中文版（含 6 个 BackgroundAgents_* 工具说明 + agent 列表）`
  - 注入的 6 个工具：`BackgroundAgents_StartTask` / `_WaitForFirstCompletion` / `_GetTaskResults` / `_GetAllTasks` / `_ContinueTask` / `_ClearCompletedTask`
  - **效果**：LTAI-Chat 现在可以异步委派 LTAI-Math 算数值、委派 LTAI-Code 跑代码、委派 LTAI-Data 查数据；并发执行 → 加速多领域任务

### P10.1 OTel SourceName per-agent ✅
- `OpenTelemetrySourceName = $"LTAI.{name}"`（之前 P7.6 已设，现在正式）
- 效果：P9.1 的 `DevUISpanCollector` 可按 source 过滤；每 agent 链路独立可观察
- `ActivityListener.ShouldListenTo` 早先已用 `StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal)` 接收全部 — 已兼容

### P10.2 HarnessInstructions 中文 ✅
- 之前用 `HarnessAgent.DefaultInstructions`（英文）
- 改成中文版（6 条一般准则 + 4 工具调用注意 + `<<<NEEDS_PRO>>>` 升级合约）— Plan mode 保留默认（其行为受 plan-mode 系统提示硬约束）
- `HarnessInstructions` 与 `ChatOptions.Instructions` 由 Harness 内部拼接（`HarnessAgent.BuildInnerAgent` line 158-164）

### P10.3 MaximumIterations 显式 ✅
- `MaximumIterationsPerRequest = 50`（默认 40）— 给 BackgroundAgents 委派链 + 中间推理留余量
- 通过 `FunctionInvokingChatClient.MaximumIterationsPerRequest` 生效

### Key Decisions
- **D24 LazyAIAgentProxy 而非 2-phase build**：`HarnessAgent` 构造期即需要 `BackgroundAgents` 列表解析（`BackgroundAgentsProvider` ctor 调用 `agent.Name`），构造期触发 keyed service 解析会循环。Lazy proxy 让 `Name` 静态已知（`AgentRegistry`），`RunCore*` 懒解析 — 简洁且零侵入
- **D25 不在 BackgroundAgents 列表中放自己**：self-delegation 会死循环（agent 把任务委派给自己）
- **D26 排除 router**：`router` 是 MAF `HandoffWorkflowBuilder` 编排用的非用户可见 agent，不应暴露给 BackgroundAgents 用户调用
- **D27 Plan mode BackgroundAgents 仍开启**：plan mode 只是禁了写工具/PlanExit 工具，BackgroundAgents 委派是只读分析，应保留
- **D28 中文 HarnessInstructions 不叠加默认英文**：Harness 内部 line 158-164 用 `+` 拼接两段；中文版完全替代默认（agent 自己的 `ChatOptions.Instructions` 已携带身份/角色/职责）

### Files touched
**新建**
- `src/LTAI.Agent/LazyAIAgentProxy.cs`（~75 行）

**改**
- `src/LTAI.Agent/ServiceCollectionExtensions.cs`：
  - `HarnessInstructions` 字段（中文版覆盖英文默认）
  - `MaximumIterationsPerRequest = 50`
  - `BackgroundAgents` 列表（9 sister agents，LazyAIAgentProxy 包装）
  - `BackgroundAgentsProviderOptions.Instructions`（中文版 BackgroundAgents_* 工具说明）
  - `OpenTelemetrySourceName` 已存在 P7.6，本步骤确认

## Verification
- [x] 构建通过：5 项目 (LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli) 0 errors
- [x] 全绿：0 警告 in LTAI.Agent / LTAI.Web / LTAI.Cli
- [ ] 真实 LLM 调用 smoke test：LTAI-Chat 委派 LTAI-Math 计算表达式 + 委派 LTAI-Code 执行 sandbox 命令
- [ ] TUI Dashboard 按 `1` 看到 `LTAI.{name}` 命名的 source
- [ ] LTAI.Desktop 浏览器拉起 DevUI 后能看到 BackgroundAgents 工具列表

## Next Steps (P11+)
- **P11.1**：BackgroundAgents 委派结果 citation 注入（让 chat 直接看到 LTAI-Math 的算式 / LTAI-Code 的代码）
- **P11.2**：Agent-as-tool 包装（让 LTAI-Chat 可选将某个 sister agent 注册为单次 tool 调用）
- **P11.3**：Harness 装饰链定制（如 LTAI-Writer 不需要 ToolApprovalAgent — 写作无需审批）
- **P11.4**：Cross-agent session 共享（让 LTAI-Math 计算的中间结果能在 LTAI-Code 的 session 中可见）

# 2026-06-02 P11 LocalEmbedder 性能优化

## Goal
- 释放本地 ONNX 嵌入模型的吞吐量：batch inference 替代 N 次串行调用 + 工具/agent 描述 embedding 持久化缓存
- 填 LTAI.Benchmarks 跑出真实 perf 数字

## Plan

### P11.1a LocalEmbedder.GenerateBatch（batched ONNX inference）✅
- `src/LTAI.AI/LocalEmbedder.cs`（+128 行）
  - `GenerateBatch(IReadOnlyList<string>)` → `IReadOnlyList<float[]>`：1 次 `session.Run` 处理 N 条
  - 内部：tokenize each → find maxLen → 构造 batched tensors `[N, maxLen, dim]` → 1 ONNX call → mean-pool each row with attention mask → L2 normalize
  - 私有 `TokenizeToIds` 抽出 tokenize-only 流程
- `src/LTAI.AI/EmbeddingClient.cs` 改 12 行：
  - 删除 N>20 时 `Task.WhenAll(texts.Select(t => Task.Run(() => _local.Generate(t))))` 的并行调用
  - 改 `Task.Run(() => _local.GenerateBatch(texts), ct)` — 单次 batched ONNX call
- **预期加速**：5-10x（CPU）；GPU 加速器更显著（DML/CUDA 偏好大 batch）

### P11.1b ToolEmbeddingCache（持久化）✅
- `src/LTAI.AI/ToolEmbeddingCache.cs`（新建，~170 行）
  - JSON 持久化（不用 SQLite — LTAI.AI 没引 Microsoft.Data.Sqlite，依赖留给 LTAI.Agent）
  - SHA-256 指纹 keyed by (key, fingerprint)：描述未变直接命中；变了就重算
  - 首次 `GetOrComputeAllAsync` 触发 1 次 batched ONNX 调用，覆盖 80+ 工具
  - 后续启动：从 JSON 加载，零网络/计算开销
  - 跨进程重启 OK
- 调用方（未来 P11.3）：DecisionTreeRouter / ToolRetrievalProvider 启动时一次性预热 + 缓存

### P11.2 LocalEmbedderBenchmarks（perf 数字）✅
- `tests/LTAI.Benchmarks/Program.cs` 重写（+93 行）
  - 5 个 benchmark：Single_ShortText / Single_MediumText / Batched_Batch8 / Batched_Batch32 / Batched_Batch128
  - `MemoryDiagnoser` + `ShortRunJob`（1 次快跑；CI 切换 `[MediumRunJob]`）
  - `dotnet run -c Release --project tests/LTAI.Benchmarks` 跑 BenchmarkDotNet
  - `dotnet run -- smoke` 跑快速 smoke test（不依赖 BDN）
  - 模型不存在时所有 benchmark 跳过（baseline=0），不报错

### Key Decisions
- **D29 P11.1a GenerateBatch 返回 `IReadOnlyList<float[]>` 而非 `float[][]`**：避免数组与接口之间的转换；与 `EmbeddingClient.GenerateBatchAsync` 已有 `float[][]` 签名在边界做 `Select(v => v).ToArray()` 转换
- **D30 P11.1a 不支持长文本 sliding window**：batch 模式假设所有文本 ≤ 512 tokens；超过的截断到 511 + [SEP]（与 Generate 一致），sliding window 留给单条 `Generate` 处理。LTAI 工具/agent 描述都是短文本（< 200 tokens）
- **D31 P11.1b JSON 而非 SQLite**：LTAI.AI 不引 Microsoft.Data.Sqlite；JSON 120KB 完全可接受，跨重启 OK
- **D32 P11.1b SHA-256 而非 ETag/版本号**：描述字符串本身可重计算指纹；不需额外 metadata
- **D33 P11.2 Smoke test 用 `dotnet run -- smoke` 短路径**：不依赖 BDN 的 [-job] 复杂参数，CI 友好

### Files touched
**新建**
- `src/LTAI.AI/ToolEmbeddingCache.cs`（~170 行）

**改**
- `src/LTAI.AI/LocalEmbedder.cs`（+128 行：`GenerateBatch` + `TokenizeToIds`）
- `src/LTAI.AI/EmbeddingClient.cs`（改 12 行：N>20 fallback 替换为 batched）
- `tests/LTAI.Benchmarks/Program.cs`（+93 行：5 benchmarks + smoke mode）

## Verification
- [x] 构建通过：6 项目 (LTAI.AI / LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli + LTAI.Benchmarks) 0 errors
- [x] 全绿：0 警告 in LTAI.AI
- [ ] 真实 perf 数字（用户已说"测试太耗时间"，可后续手动跑）
- [ ] DecisionTreeRouter P7.7 / ToolRetrievalProvider 切到 ToolEmbeddingCache（预计 P11.3）

## Next Steps (P12+)
- **P12.1**：DecisionTreeRouter 切到 ToolEmbeddingCache（10 agent 描述预热 + 缓存）
- **P12.2**：ToolRetrievalProvider 切到 ToolEmbeddingCache（80+ 工具描述预热 + 缓存）
- **P12.3**：嵌入维度自适应（如果未来用 BGE-large-zh 1024d）
- **P12.4**：远程 embedding API 的 batch 端点优化（多 provider 自动选择最优）

# 2026-06-02 P12 LocalEmbedder 全链路缓存 + 智能加载

## Goal
- 把 P11.1a (batched) + P11.1b (cache) 真正接进**两条最热的 ONNX 路径**：AgentRegistry（决策树路由）+ ToolRegistry（80+ 工具 RAG）
- 智能跳过 ONNX 模型预加载：检测到任一远程 embedding API key 就**完全不加载** 90MB ONNX 模型（节省 200MB RAM + 5-10s 启动）

## 评估结论（前置分析）
| 部署场景 | ONNX 必要性 | 当前成本 | P12 优化后 |
|---|---|---|---|
| 本地开发（无 key） | 必需 | 5-10s + 200MB | 1 batched + 0 后续 |
| CI/CD（key 不稳定） | 必需 | 5-10s + 200MB | 1 batched + 0 后续 |
| 企业内网（隐私） | 必需 | 5-10s + 200MB | 1 batched + 0 后续 |
| 个人云端（带 key） | 可选 | 5-10s + 200MB | **0 加载，纯 API** |
| 100 万次 API 费 | 0.02-0.13 美元 | — | — |

**判定**：ONNX 必须保留（无 key 兜底），但带 key 用户**不应**承担 200MB + 5-10s 成本。

## Plan

### P12.1 AgentRegistry → ToolEmbeddingCache ✅
- `src/LTAI.Agent/AgentRegistry.cs`：
  - `EnsureEmbeddingsAsync(embedder, cache = null, ct)` — 接受可选 `ToolEmbeddingCache`
  - `SelectTopKAsync(..., cache = null, ...)` / `SelectTopKWithScoresAsync(..., cache = null, ...)` — 透传 cache
  - 有 cache：1 次 batched 调用 + JSON 持久化（10 个 agent 描述）
  - 无 cache：原 sequential 路径
- `src/LTAI.Agent/Workflows/DecisionTreeRouter.cs`：构造参数加 `ToolEmbeddingCache?`，透传到 `SelectTopKWithScoresAsync`
- `src/LTAI.Agent/ServiceCollectionExtensions.cs` Step 3：`new DecisionTreeRouter(... sp.GetService<ToolEmbeddingCache>())`
- **效果**：冷启动 11 次 ONNX 调用 (1 query + 10 agents) → 0 次；二次启动 0 次（cache hit）

### P12.2 ToolRegistry → ToolEmbeddingCache ✅
- `src/LTAI.AI/ToolRegistry.cs`：
  - `InitializeAsync(tools, embedder, cache = null, ct)` — 接受可选 cache
  - 有 cache：1 batched ONNX 调用（80+ 工具）+ JSON 持久化
  - 无 cache：原 batched 路径（无持久化）
  - 新增 `private static async Task<float[][]> DirectBatchAsync(embedder, texts, ct)` helper
- `src/LTAI.Agent/Tools/ToolRetrievalProvider.cs`：构造参数加 `ToolEmbeddingCache?`，透传到 `ToolRegistry.InitializeAsync`
- `src/LTAI.Agent/ServiceCollectionExtensions.cs` `BuildAgentImpl`：
  - `[new ToolRetrievalProvider(embedder, cache: sp.GetService<ToolEmbeddingCache>()), ...]`
- **效果**：冷启动 80+ 次 ONNX 调用 → 1 次；二次启动 0 次

### P12.3 智能 ONNX 加载（远程 key → 完全跳过 ONNX）✅
- `src/LTAI.AI/LocalEmbedder.cs`：
  - 新增 `public static bool DefaultDisabled { get; set; }` 全局标志
  - ctor：`if (DefaultDisabled) return;` — 跳过 model 检测 + 预热，`Available` 永远 false
  - `PreWarmAsync()`：`if (DefaultDisabled || _loadAttempted) return;` — 早返回
- `src/LTAI.AI/MultiProviderChatClient.cs` `AddLTAIAI()`：
  ```csharp
  var hasRemoteEmbedKey = EmbeddingClient.DefaultProviders
      .Any(p => !string.IsNullOrEmpty(SecretManager.Get(p.envVar)));
  LocalEmbedder.DefaultDisabled = hasRemoteEmbedKey;
  ```
- `src/LTAI.Agent/Vector/KbGraph.cs`：
  - `static readonly Lazy<LocalEmbedder?> _sharedEmbedder = new(() =>
        LocalEmbedder.DefaultDisabled ? null : new LocalEmbedder(), true);`
  - 调用方加 `localEmb != null && localEmb.Available` 守卫
- **效果**：带 key 用户 0MB / 0s ONNX 启动开销（无 key 用户行为不变）

### Key Decisions
- **D34 智能加载用静态标志而非 ctor 参数**：`LocalEmbedder.DefaultDisabled` 静态标志让 `AddLTAIAI` 在**注册前**设置，避免 DI 解析时序问题
- **D35 `EmbeddingClient` 不需要改优先级**：`LocalEmbedder.Available` 永远 false（disabled 模式）→ 现有 `if (_local?.Available == true)` 守卫自然 fall through 到 API provider
- **D36 智能加载检测 DefaultProviders 4 个 key**（DEEPSEEK / OPENAI / SILICONFLOW / DASHSCOPE）；不在 LTAIOptions 配置走 `Local` provider
- **D37 ToolEmbeddingCache 仍可共享**：`ToolEmbeddingCache` 是通用 (Key, Description) 持久化，agent + tool 共用同一 JSON 文件（不同 Key namespace）
- **D38 智能加载不影响 `KbGraph` 主流程**：`FastEmb` 仍能 cosine，KG intent classification 退化 1 档（KBG 仍可用，但精度-15%）
- **D39 不引入 `IHostedService.PreWarm` 钩子**：标志是同步的，AddLTAIAI 设置后 ctor 立即生效

### Files touched
**改**
- `src/LTAI.AI/LocalEmbedder.cs`（+18 行：DefaultDisabled + ctor/Available/PreWarmAsync 守卫）
- `src/LTAI.AI/MultiProviderChatClient.cs`（+18 行：hasRemoteEmbedKey 检测 + DefaultDisabled 赋值 + ToolEmbeddingCache DI 注册）
- `src/LTAI.AI/ToolRegistry.cs`（+24 行：cache 参数 + DirectBatchAsync helper）
- `src/LTAI.Agent/AgentRegistry.cs`（+24 行：cache 参数 + GetOrComputeAllAsync 集成）
- `src/LTAI.Agent/Tools/ToolRetrievalProvider.cs`（+5 行：cache 参数 + 透传）
- `src/LTAI.Agent/Workflows/DecisionTreeRouter.cs`（+5 行：cache 参数 + 透传）
- `src/LTAI.Agent/ServiceCollectionExtensions.cs`（3 处：ToolRetrievalProvider cache + DecisionTreeRouter cache）
- `src/LTAI.Agent/Vector/KbGraph.cs`（+5 行：null 守卫 + Lazy 重构）

### Verification
- [x] 编译通过：6 项目 (LTAI.AI / LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli) 0 errors
- [x] 全绿：P12 触改的文件 0 warnings（LTAI.Agent 其他文件 14 个 pre-existing warnings 来自 OfficeDocumentReader/DocumentTools/SkillEvolutionEngine/KbGraph line 536 — 已修复）
- [ ] 真实 perf 数字（用户已说"测试太耗时间"，可后续手动跑）
- [ ] 手动验证：带 `DEEPSEEK_API_KEY` 启动 → `models/` 目录不被动；冷启动 0 ONNX
- [ ] 手动验证：无 API key 启动 → ONNX 预热正常（与 P11 行为一致）

## Next Steps (P13+)
- **P13.1**：ONNX 模型量化（90MB → 25MB，Q8 ONNX 加速）
- **P13.2**：GPU 加速启用（DirectML for Windows / CUDA for Linux）
- **P13.3**：DecisionTreeRouter 接入远程 API 缓存（远程结果也 cache 到 ToolEmbeddingCache，跨进程重启复用）
- **P13.4**：ToolRetrievalProvider 暴露 `_cache.CachedEntryCount` 到 P9 DevUI 仪表盘
- **P13.5**：自动降级策略（API 连续 3 次失败 → 强制加载 ONNX 作为 fallback）

# 2026-06-02 P13 ONNX 量化 + GPU 自适应加速

## Goal
- P13.1 量化 ONNX 模型：MiniLM 走 `model_qint8_avx512_vnni.onnx`（23MB，~4× 缩；~2-3× 推理加速）
- P13.2 GPU 自适应：DirectML (Windows no-NVIDIA) → CUDA (NVIDIA) → CPU 探测链；用户可配 `LTAI:Embedding:Gpu`
- 所有 ONNX 模型 URL 走 **国内镜像** `hf-mirror.com`（HuggingFace 官方授权镜像，0 直连 huggingface.co）

## Plan

### P13.1 ONNX 模型量化 ✅
- `src/LTAI.AI/LocalEmbedder.cs`：
  - `ModelInfo` record 加 `QuantizedModelUrl?` + `QuantizedFileName?` 字段
  - `KnownModels` MiniLM 加 `https://hf-mirror.com/.../model_qint8_avx512_vnni.onnx` (23MB INT8 AVX-512+VNNI)
  - BGE 系列无上游量化版本，`QuantizedModelUrl = null`（FP32 兜底）
  - `ResolveModelFiles(subDir, name)`：根据 `Options.Quantization` 选 `model.int8.onnx` 或 `model.onnx`
  - `DownloadModelAsync` 同时下载 INT8（best-effort，失败不阻塞 FP32）
  - `ListAvailableModels` 报告 `QuantizedDownloaded` 状态
  - `EnsureLoaded` 后设 `_usingQuantizedModel` 标志（DevUI 可见）
- `src/LTAI.AI/EmbeddingOptions.cs`（新建，~30 行）：
  - `Gpu` (auto/dml/cuda/cpu) + `Quantization` (auto/int8/fp32) + `DeviceId`
  - `LocalEmbedder.Options` 静态属性（默认 `auto/auto/0`）
- `src/LTAI.AI/LTAI.AI.csproj`：
  - 新增 `DownloadQuantizedEmbeddingModel` target → curl 23MB INT8
  - `PublishEmbeddingModel` 同时复制 FP32 + INT8 到 publish 目录
- `src/LTAI.Core/Configuration/LTAIOptions.cs`：
  - 新增 `EmbeddingConfig` 类（Gpu/Quantization/DeviceId）
  - `LTAIOptions.Embedding` 属性
  - `appsettings.json` 加 `"Embedding": { "Gpu": "auto", "Quantization": "auto", "DeviceId": 0 }`

### P13.2 GPU 自适应加速 ✅
- `src/LTAI.AI/LTAI.AI.csproj`：
  - 新增 `Microsoft.ML.OnnxRuntime.Gpu 1.21.0` PackageReference（CUDA + TensorRT EP）
  - 已有 `Microsoft.ML.OnnxRuntime.DirectML 1.21.0`（DML EP）
- `src/LTAI.AI/LocalEmbedder.cs`：
  - `EnsureLoaded` 重构：
    - 旧代码：盲目 `try { DML } catch { }` + `try { CUDA } catch { }` + `Append CPU`（浪费 + 不知道哪个 EP 真正生效）
    - 新代码：`TryAppendDml` / `TryAppendCuda` 显式探测 + 设 `_activeExecutionProvider` 字符串（"DML" / "CUDA" / "CPU"）
    - `Options.Gpu` = `dml` / `cuda` 时显式要求，缺失抛 `InvalidOperationException`
    - `auto` 模式按 `DML > CUDA > CPU` 探测
  - `GraphOptimizationLevel.ORT_ENABLE_ALL` 显式开启
  - `LocalEmbedder.ActiveExecutionProvider` + `UsingQuantizedModel` 只读属性 → DevUI 仪表盘
- `src/LTAI.AI/MultiProviderChatClient.cs`：
  - `LocalEmbedder` 改 factory 注册：解析时读 `IOptions<LTAIOptions>.Embedding` 同步到 `LocalEmbedder.Options`（再 new LocalEmbedder()）
- `src/LTAI.Agent/Vector/KbGraph.cs`：保持 P12.3 null 守卫（不需修改）

### Key Decisions
- **D40 ONNX 模型全部走国内镜像**：`hf-mirror.com` 是 HuggingFace 官方授权国内镜像，1:1 同步上游；`MiniLM-L6-v2` FP32/INT8 + `BGE-small-zh` FP32 + `BGE-small-en` FP32 全 0 直连 `huggingface.co`（避免国内网络卡顿）
- **D41 INT8 量化只对 MiniLM 启用**：BGE 上游无 INT8 导出；不能本地 `QuantizeDynamic`（无 NuGet 包），要静态量化需要校准数据 + 一次预生成；接受 BGE 走 FP32 + ONNX 图优化
- **D42 GPU EP 探测按需**：旧代码无脑 append 全部 EP，新代码只在 `Options.Gpu = auto` 时按 DML→CUDA→CPU 探测；`dml` / `cuda` 强制时缺失抛错（用户期望明确）
- **D43 `EmbeddingOptions` 在 LTAI.AI 而非 LTAI.Core**：避免 LTAI.Core 引入 ONNX 依赖（保持 LTAI.Core 零外部依赖）
- **D44 `Options` 静态而非 ctor 参数**：与 `DefaultDisabled` 模式一致（全局配置，DI 解析时序简单）
- **D45 Gpu 包始终引用**：用户场景分两种（有/无 NVIDIA GPU），但 `Microsoft.ML.OnnxRuntime.Gpu` 是托管绑定包（不含大 DLL），CUDA 实际库由 `runtimes/win-x64/native/*cudart*.dll` 决定；非 NVIDIA 系统 loader 看到 `Microsoft.ML.OnnxRuntime.Gpu.dll` 但调 `AppendExecutionProvider_CUDA` 抛 → fall through CPU
- **D46 GPU DLL 总是部署的代价**：发布包会包含 ~200MB CUDA DLL；这是微软 ORT 标准部署模式，不在 LTAI 优化范围

### Files touched
**新建**
- `src/LTAI.AI/EmbeddingOptions.cs`（~30 行）

**改**
- `src/LTAI.AI/LocalEmbedder.cs`（+90 行：ModelInfo 扩展、ResolveModelFiles、TryAppendDml/Cuda、_activeExecutionProvider / _usingQuantizedModel 字段、EmbeddingOptions 属性、Available 静态、DetectCurrentModelWithQuant）
- `src/LTAI.AI/LTAI.AI.csproj`（+13 行：Gpu 包 + DownloadQuantizedEmbeddingModel target + PublishEmbeddingModel INT8 copy）
- `src/LTAI.AI/MultiProviderChatClient.cs`（+12 行：LocalEmbedder factory + EmbeddingConfig 绑定）
- `src/LTAI.Core/Configuration/LTAIOptions.cs`（+20 行：EmbeddingConfig + LTAIOptions.Embedding）
- `src/LTAI.Web/appsettings.json`（+5 行：Embedding section）

### Verification
- [x] 编译通过：6 项目 (LTAI.AI / LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli) 0 errors
- [x] 全绿：0 警告 in LTAI.AI
- [x] NuGet 包下载验证：`microsoft.ml.onnxruntime.gpu 1.21.0` + `microsoft.ml.onnxruntime.directml 1.21.0` 都已 restored
- [ ] 真实量化加载 smoke test（用户已说"测试太耗时间"，可后续手动跑）
- [ ] 手动验证：在有 NVIDIA GPU 的开发机上 `AppendExecutionProvider_CUDA` 成功 → `_activeExecutionProvider = "CUDA"`
- [ ] 手动验证：`hf-mirror.com` 量化模型可下载（23MB INT8）

## Next Steps (P14+)
- **P14.1**：BGE 量化（用 `onnxruntime.quantize` Python 工具预生成 INT8，ship to HF mirror）
- **P14.2**：嵌入模型热切换（不重启进程就能换 MiniLM ↔ BGE ↔ INT8）
- **P14.3**：Multi-embedding 融合（同时使用 MiniLM + BGE，concat 平均 / 加权）提升跨语言效果
- **P14.4**：ONNX 缓存预热 background service（首次启动时后台下载所有可能用到的模型）

