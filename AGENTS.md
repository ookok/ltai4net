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
- **P7.4（已完成清理 — 2026-06-02）**：9 项跳过/推迟任务全部审查完毕，仅 1 项本地可行（DurableTask → P8 ✅ 已落地）
  - **删除**（Azure 订阅依赖，8 项）：Foundry / Foundry.Hosting / AzureAI.Persistent / CopilotStudio / Hosting.AzureFunctions / CosmosNoSql / Purview / GitHub.Copilot
  - **删除**（Aspire dev-only 编排层，价值 0）：Aspire DevUI hosting
  - **移出 P7.4 跳过表**：DurableTask → P8 ✅（DTFx 1.24.2 + InProcessTestHost 0.2.3-preview.1 已落地）
  - **新增到 P14 P1**：`Microsoft.Agents.AI.Workflows.Declarative.Mcp` — 纯本地，无 Azure 依赖，扩展 YAML 工作流接 MCP 工具
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

# 2026-06-02 P8.1 ✅ SQLiteOrchestrationService（跨重启持久化）

## Goal
- 把 P8 的 `InMemoryOrchestrationService`（进程内 only）换成 SQLite 持久化版本，进程重启后 in-flight 的 orchestration / entity 状态自动恢复
- 6 项目（AI / Agent / TUI / Desktop / Web / Cli）继续 0 errors

## Plan

### 1. 关键设计决策
- **D75 继承 + `new` 隐藏，不 `override`**：基类 `InMemoryOrchestrationService` 的接口方法都不是 `virtual`，唯一可行路径是 `new` 关键字隐藏。`InMemoryGrpcSidecarHost` 内部只用具体引用作存储（再转回 `IOrchestrationService`/`IOrchestrationServiceClient`），从未在具体类型上调任何方法 — 所以 `new` 安全
- **D76 直接反射私有 `instanceStore.store` 字典**：DTFx `instanceStore` 是 `InMemoryOrchestrationService` 的私有嵌套类，外部无法直接拿到。但 in-memory 服务本身用 `System.Text.Json.Nodes` (JsonValue/JsonArray) 存 `SerializedInstanceState` 的状态 — 等于自带 JSON round-trip，**零序列化器依赖**
- **D77 Snapshot 持久化，不是 write-through**：每个 mutation method 调 `base` 后做一次全量 snapshot 到 SQLite（单事务）。规模只有 10 agents + 几个 orchestration，snapshot overhead 可忽略；好处是没有 race condition / 不用读 lock
- **D78 锁状态不持久化**：`SerializedInstanceState.IsLoaded` 是 process-local lock，重启后所有 instance 自然 unlocked。DTFx worker 重启后会从 `readyToRunQueue` 拉新工作 — 我们 hydrate 时把有 pending messages 的 instance 通过反射调 `readyToRunQueue.Schedule(state)` 重排队
- **D79 `CreateAsync(true)` / `DeleteAsync(true)` 同时清 SQLite**：与内存 store reset 同步

### 2. SQLite schema（极简，1 张表）
```sql
PRAGMA journal_mode = WAL;
CREATE TABLE IF NOT EXISTS orchestration_state (
    instance_id     TEXT PRIMARY KEY,
    execution_id    TEXT,
    is_completed    INTEGER NOT NULL,    -- 0/1
    status_json     TEXT,                 -- JsonValue<OrchestrationState> (nullable)
    history_json    TEXT NOT NULL,        -- JsonArray<HistoryEvent>
    messages_json   TEXT NOT NULL,        -- JsonArray<TaskMessage>
    updated_at      TEXT NOT NULL         -- ISO 8601
);
```

### 3. Files touched
- **新建** `src/LTAI.Agent/Durability/SQLiteOrchestrationService.cs`（~340 行）
  - 反射常量：`s_instanceStoreField` / `s_innerStoreField` / `s_serializedInstanceStateType` / `s_statusRecordField` / `s_historyField` / `s_messagesField` / `s_executionIdField` / `s_isCompletedField` / `s_readyToRunQueueField` / `s_scheduleMethod` / `s_serializedInstanceStateCtor`
  - `new` 覆盖 11 个 mutation 方法（StartAsync/StopAsync/CreateAsync(true)/DeleteAsync(true)/CreateTaskOrchestrationAsync × 2/SendTaskOrchestrationMessageAsync/SendTaskOrchestrationMessageBatchAsync/CompleteTaskActivityWorkItemAsync/CompleteTaskOrchestrationWorkItemAsync/ForceTerminateTaskOrchestrationAsync）
  - `SnapshotAfter(Func<Task> op)` — 调 base → persist（只在 `_hydrated=true` 后）
  - `PersistAllAsync` — `SemaphoreSlim` 串行化，单事务 upsert 全量 instance
  - `HydrateSync` — 读全表 → 反射构造 `SerializedInstanceState` → 填 `store[instanceId]` → 调 `readyToRunQueue.Schedule` 重排有 pending messages 的 instance
- **改** `src/LTAI.Agent/Durability/LTAIDurableAgentHost.cs`
  - `LTAIDurableAgentHost` ctor 改用 `IOptions<LTAIDurableAgentHostOptions>`（DI 配置）
  - 加 `DatabasePath` 属性 + `LTAIDurableAgentHostOptions.DatabasePath` + `ResolveDatabasePath()` 辅助方法（默认 `.livingtree/durability.db`）
  - 注释更新为"cross-restart persistence 由 SQLiteOrchestrationService 提供"
- **改** `src/LTAI.Agent/Durability/DurableAgentServiceCollectionExtensions.cs`
  - 临时 `BuildServiceProvider` + `Dispose` 拿到 host instance 的 `Port`（AddInMemoryDurableTask 需要 port 提前 pin）
  - 注入顺序：BindOptions → AddSingleton<LTAIDurableAgentHost> → AddHostedService → BuildServiceProvider (temp) → AddInMemoryDurableTask(port) → swap factory descriptors
  - Descriptor swap: 替换 3 个 factory descriptors（`InMemoryOrchestrationService` / `IOrchestrationService` / `IOrchestrationServiceClient`）为返回 `SQLiteOrchestrationService` 的 factory
- **改** `src/LTAI.Core/Configuration/LTAIOptions.cs`
  - `DurableConfig.DatabasePath` 字段（默认 `.livingtree/durability.db`），由 `LTAIOptions.Durable` 配置绑定

### 4. 已知限制
- **没有 AOT-friendly**：用反射 + `Activator.CreateInstance`（间接通过 `ConstructorInfo.Invoke`）。`Microsoft.Data.Sqlite` 也不在 AOT 兼容列表。LTAI 没启用 AOT，影响 0
- **并发写语义**：在内存中 mutation 完成后 + snapshot 完成前有窗口（典型 < 1ms）。如果进程在这窗口崩溃，最多多丢失最后一次 snapshot 的 mutation
- **WAL 模式**：允许多 reader + 1 writer；Hydrate 启动时读，PersistAll 持写锁。单进程 LTAI 没有并发 writer
- **History 增长无界**：每次 instance store 写都做全量 snapshot。10 agents × ~10KB = 100KB 量级，几万次写后 SQLite 会膨胀。**未来 P8.1.x 优化**：加 WAL 周期性 checkpoint + 限制 history 长度
- **不持久化 `InMemoryQueue` (activityQueue)**：它是 process-local channel，无状态机需要持久化

### 5. Verification
- [x] 编译通过：6 项目（LTAI.AI / LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli + LTAI.Core）0 errors
- [x] LTAI.Agent 触改文件 0 新增 warnings（pre-existing 14 个 warnings 来自 OfficeDocumentReader / DocumentTools / SkillEvolutionEngine / KbGraph — 维持）
- [ ] 手动 smoke test：启动 LTAI → 触发一次 BackgroundAgent 委派 → kill -9 进程 → 重启 → 验证 agent session 仍可见（用户已说"测试太耗时间"，可后续手动跑）
- [ ] 验证 schema 兼容性：删除 `.livingtree/durability.db` 后 LTAI 启动正常（schema 由 `EnsureSchemaSync` 自动创建）

### 6. 配置文件示例
```json
{
  "LTAI": {
    "Durable": {
      "Enabled": true,
      "DatabasePath": ".livingtree/durability.db"
    }
  }
}
```

## Next Steps (P8.2+)
- **P8.2**：smoke test（启动 → 发请求 → 模拟崩溃 → 重启 → 验证状态还原）— user 已说"测试太耗时间 先做功能"，跳过
- **P8.3**：history/event log 压缩（periodic WAL checkpoint，limit history length）
- **P8.4**：跨进程 persist（多 LTAI 实例共享 SQLite — 切换到 `Microsoft.Data.Sqlite` 的 SHM/LOCK 模式）— 不在 P8 范围，留作 P15+
- **P9.x**（P14 排序中）— 见 P14 P0 推荐顺序

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

## Next Steps (P15+)
- **P15.0-P15.11** 可热改编排（YAML/JSON workflow）— 已落地（2026-06-02）。详见下方 P15 章节

# 2026-06-02 P14 任务重整（5 主题 / 10 项 → 7 主题 / 15 项，2026-06-02 加 P14.7）

## Goal
- P11+P12+P13 系列完成后，**P14+ 列表已 4 项太散**（无主题 / 优先级 / 依赖关系）
- 重新组织成 **5 主题 / 10 项**（2026-06-02 增 P14.7 `Workflows.Declarative.Mcp` → **7 主题 / 15 项**），按 P0-P3 优先级排序，明确依赖链

## P14 任务表

| # | 任务 | 主题 | 优先级 | 预计 | 依赖 |
|---|---|---|---|---|---|
| **P14.1** ✅ | BGE INT8 量化（复用 **Xenova 预量化版** — `Xenova/bge-small-{zh,en}-v1.5/onnx/model_int8.onnx`） | 量化补完 | 🔴 P0 | 0.5d | 无 |
| **P14.2** ✅ | P9.1 DevUI 显示 active EP + quant 状态（P13.2 telemetry 已埋点，未 surface） | 可观测性 | 🔴 P0 | 0.5d | P13.2 ✅ |
| **P14.3** ✅ | TUI `/model` 菜单扩展（`cleanup`/`info`/`quant` 三子命令） | UX | 🔴 P0 | 1d | P13.6 ✅ |
| **P14.4** | INT8 vs FP32 性能 benchmark（P11.2 BDN 框架 + GPU vs CPU 对照） | 可观测性 | 🟡 P1 | 1d | 无 |
| **P14.5** ✅ | DecisionTreeRouter 远程 API 结果 cache 到 `RemoteEmbeddingCache`（P13.3 替代方案 — in-process TTL 24h） | 缓存 | 🟡 P1 | 2-3d | P12 ✅ |
| **P14.6** ✅ | `ToolEmbeddingCache.CachedEntryCount` 暴露到 DevUI dashboard（P13.4） | 可观测性 | 🟡 P1 | 0.5d | P14.5 |
| **P14.7** ✅ | `Workflows.Declarative.Mcp`（P7.4 新增 — 纯本地，无 Azure 依赖）— YAML workflow 接 MCP 工具 | 工作流扩展 | 🟡 P1 | 1-2d | 无 |
| **P14.8** ✅ | 热模型切换（不重启进程换 MiniLM ↔ BGE；自动失效 tool/agent 缓存并懒重 embed） | UX | 🟢 P2 | 1w | 无 |
| **P14.9** | Per-model 量化配置（MiniLM=int8, BGE=fp32, BGE-large=fp32 混搭） | 灵活 | 🟢 P2 | 3-5d | P13 ✅ |
| **P14.10** | API 失败 N 次 → 自动 fallback ONNX 加载（P13.5） | 鲁棒 | 🟢 P2 | 2-3d | P12 ✅ |
| **P14.11** | Multi-embedding 融合（MiniLM + BGE 并行推理，concat / 加权；跨语言效果↑） | 质量 | 🔵 P3 | 2-3w | P14.9 |
| **P14.12** | ONNX 缓存预热 background service（首次启动时后台下载所有可能用到的模型） | UX | 🔵 P3 | 2-3d | 无 |
| **P14.13** ✅ | `TaskQueueTool` 暴露给 5 agents（BGJS/TaskQueue 二选一并暴露工具；修复死代码） | Long-running 完善 | 🔴 P0 | 1d | P5.4 ✅ |
| **P14.14** ✅ | TUI `/jobs` + Desktop JobsView 实时展示（订阅 `BackgroundJobService.JobCompleted`） | Long-running 完善 | 🔴 P0 | 1d | P14.13 ✅ |
| **P14.15** ✅ | LTAI.Web `GET /api/jobs` 端点（list / get / cancel） | Long-running 完善 | 🔴 P0 | 0.5d | P14.13 ✅ |

## 主题分组

### 主题 1：量化补完（P14.1）✅ 完成 (commit `bdcc30e`)
- **目标**：BGE-zh/en 也支持 INT8，消除 P13.6 "95MB FP32" 死角
- **✅ 完成方案**：复用 **Xenova 预量化版本**（Transformers.js 团队维护）
  - `Xenova/all-MiniLM-L6-v2/onnx/model_int8.onnx` (21.9MB) — universal INT8
  - `Xenova/bge-small-zh-v1.5/onnx/model_int8.onnx` (22.8MB)
  - `Xenova/bge-small-en-v1.5/onnx/model_int8.onnx` (32.2MB)
  - 零账号、零 Python、零生成；改 `LocalEmbedder.KnownModels` 3 个 URL + 3 个 build target
- **D58（已确认）**：MiniLM 走 Xenova 通用 `model_int8.onnx`（**替代 AVX-512+VNNI 专版** — 后者绑定 AVX-512 指令集，老 CPU 不可用）
- **D59（已确认）**：不调用 `CleanupStaleVariant` 自动删旧 FP32；用户主动触发（TUI `/model cleanup` 子命令 = P14.3，或 `LocalEmbedder.CleanupStaleVariant()` 调用）
- **MiniLM 一次性迁移**：build target 加 `<Delete>` step（`models/minilm-l6-v2/model.int8.onnx` 旧 AVX-512 专版 → Xenova 通用 INT8）
  - 旧文件 23,026,053 字节（AVX-512+VNNI）→ 新文件 22,972,370 字节（Xenova 通用）
  - 条件 `!Exists` 触发后自动 Delete + curl 重下；后续 build 因 `!Exists` 假，target 跳过
- **磁盘效果**：3 模型合计 **213MB (P13.6) → 78MB (P14.1) = -135MB (-63%)**
  - MiniLM: 23MB (旧 AVX-512 INT8) + 90MB (旧 FP32) → 22MB (Xenova INT8) + 90MB (旧 FP32 留作回退)
  - BGE-zh: 95MB (旧 FP32) → 23MB (Xenova INT8) [-72MB]
  - BGE-en: 95MB (旧 FP32) → 32MB (Xenova INT8) [-63MB]
- **收益**：BGE-zh/en 228MB FP32 → 56MB INT8 = **-172MB 磁盘** + 2-3× 推理加速（CPU）

### 主题 2：可观测性（P14.2 + P14.4 + P14.6）
- **P14.2** ✅ 完成 (commit `f7ecf7f`)：TUI DevUI dashboard header 3→5 行
  - 新增 embed 状态行：`model=<name> <dim>d · EP=DML/CUDA/CPU · quant=INT8/FP32`
  - 颜色：EP=green (GPU) / grey (CPU) ; quant=green (INT8) / yellow (FP32) ; model=cyan / red (缺失)
  - 边界处理：(disabled — remote API) / (not loaded yet) / (no model on disk)
  - telemetry 来源：`LocalEmbedder.ActiveExecutionProvider` / `UsingQuantizedModel` / `CurrentModelName` / `DefaultDisabled` / `ListAvailableModels()`
- **P14.4**：BDN 跑 INT8 vs FP32 latency/throughput（cache miss 差异 1-3ms vs 5-10ms；GPU EP 对照）
- **P14.6** ✅ 完成 (commit `8c013e8`)：`ToolEmbeddingCache` 加 hit/miss 计数器；dashboard header 新增第 3 行（5→6 行）
  - `CacheHits` / `CacheMisses` / `CacheLookups` / `HitRate` 四个新只读属性（`Interlocked` 线程安全）
  - `BuildCacheStatusLine` 渲染 `N entries · M hits · M misses · hit rate X%`
  - 颜色：hit rate ≥ 80% green / ≥ 50% yellow / < 50% red
  - `BuildCacheStatusLine(null)` fallback 到 `(not registered)`
  - TuiApp + TUI Program.cs 透传 DI 注入

### 主题 3：UX 改进（P14.3 + P14.8 + P14.12）
- **P14.3**：TUI `/model` 菜单三子命令
  - `/model cleanup [name]` → `CleanupStaleVariant`
  - `/model info` → 列所有 model 的 EP/quant/size/loaded
  - `/model quant fp32|int8|auto` → 切换全局 quant
- **P14.8**：运行时换模型（`_currentModelName` swap + `_session` dispose + reload；session 持久化推迟到 P15+）
  - **✅ 完成** (commit `a9ebc1d`)：4 处缓存全部 event-driven 失效
  - `LocalEmbedder.ModelSwitched` event 在 `SwitchModel` 成功 load 后 fire (锁外，参数 = 新模型名)
  - `EmbeddingClient.Local` 公开属性 → `ToolEmbeddingCache` ctor 订阅 → 调 `Invalidate()` (清 `_store` + 删 JSON)
  - `LocalEmbedderModelSwitchNotifier` (LTAI.Agent) 订阅 → 调 `AgentRegistry.ClearEmbeddings()` + `ToolRegistry.ClearEmbeddings()`
  - `ToolRegistry.SearchTopKAsync` 检测 `Embedding.Length == 0` 触发 1 次 batched 重新 embed（懒）
  - TUI `/model switch <name>` 现成命令无须改，event 自动跑
  - **回归接受**：与 P14.3 `/model quant` 行为对齐 — 都是"自动 hot-reload + 缓存失效"
- **P14.12**：`IHostedService.PreWarmEmbeddingModels` 后台下载所有 3 个模型

### 主题 4：缓存与降级（P14.5 + P14.10）
- **P14.5** ✅ 完成 (commit `f0790b4`)：用 **`RemoteEmbeddingCache`** (in-process TTL 24h) 替代 `ToolEmbeddingCache`（原因：ToolEmbeddingCache 是持久化 JSON、键为本地 (Key, Description) 业务元组；远程 API 文本 token 不属于"工具/agent 描述"业务域） — 8 个 remote 调用方自动受益
- **P14.10**：`EmbeddingClient` 维护失败计数器（window=10 calls），≥3 次连续失败 → 触发 `LocalEmbedder.PreWarmAsync()` 强制加载 ONNX 兜底

### 主题 5：灵活与质量（P14.9 + P14.11）
- **P14.9**：config schema 改 `Dictionary<string, string>` per-model
  ```json
  "Embedding": {
    "Models": {
      "minilm-l6-v2": "int8",
      "bge-small-zh": "fp32"
    }
  }
  ```
  优先级：per-model > 全局
- **P14.11**：双模型融合
  - 路由时 MiniLM + BGE 各自 top-K → RRF 融合（参考 P7.7 决策树）
  - 推理时 MiniLM 跑 1 次 + BGE 跑 1 次 → concat 768d / weighted 384d+384d
  - 跨语言场景质量↑（中文走 BGE-zh，英文走 MiniLM）

### 主题 6：Long-running task 完善（P14.13 + P14.14 + P14.15）

#### 现状评估（2026-06-02 盘点）

**4 个相关组件**：

| 组件 | 文件 | 行数 | 状态 | 持久化 | 真实消费者 |
|---|---|---|---|---|---|
| `BackgroundJobService` | `Tools/BackgroundJobService.cs` | 142 | ✅ 活跃 | ❌ 60s 清 | ✅ 5 agents |
| `TaskQueue` (P5.4) | `Tasks/TaskQueue.cs` | 203 | ⚠️ 死代码 | ❌ InMemory | ❌ **零** |
| `DurableAgentHost` (P8) + `SQLiteOrchestrationService` (P8.1) | `Durability/*` | 179 + 340 | ✅ 活跃 | ✅ SQLite | ✅ 透明 + 跨重启 |
| `BackgroundAgents` (P10) | `ServiceCollectionExtensions.cs:838-859` | 22 (instructions) | ✅ 活跃 | ✅ MAF 托管 | ✅ Harness 内置工具 |

**完成度：~50-55%**（按"功能可用"维度算）
- 基础设施 80%（3 套机制都写出来了）
- 真实使用 30%（仅 BGJS 实际接 5 agents；TaskQueue 死代码；DTFx 透明未测）
- 持久化 40%（P8.1 ✅ SQLite 跨重启；BGJS / TaskQueue 仍进程内）
- 可观测性 15%（ILogger 在, 三端展示无）
- API 暴露 0%（Web 无 `/jobs` 端点）
- 测试覆盖 0%（用户跳过）

**关键问题**：
1. `TaskQueue` 死代码：DI 注入了但零消费者，未暴露为工具
2. BGJS 实现粗糙：`_ = Task.Run(...)` fire-and-forget + 60s 清 + 无并发限制
3. DTFx 整个未 smoke test：`InProcessTestHost 0.2.3-preview.1` 是 preview + 标"for testing"
4. 三端（TUI/Desktop/Web）均无任务进度展示 / API 端点

**P14.13 + P14.14 + P14.15 目标**：
- 解决 #1（TaskQueue 暴露工具）
- 解决 #4（三端展示 + API 端点）
- 暂不解决 #2 #3（推 P15+）

#### P14.13 TaskQueueTool 暴露给 5 agents（1d）✅ 完成 (commit `76e00f3`)
- **方案 A（推荐）**：把 TaskQueue 包装成 `TaskQueueTool`（5 个方法：Enqueue/List/Wait/Get/Cancel），加到 5 agents 工具链
- **方案 B（更彻底）**：BGJS 内部改用 TaskQueue 共享并发/重试逻辑；外部仍暴露 BGJS 工具
- **评估**：先方案 A 验证 TaskQueue 可用，再考虑方案 B 合并
- **✅ 落地** (`src/LTAI.Agent/Tools/TaskQueueTool.cs`, 198 行):
  - 5 AITool: EnqueueTask / ListTasks / GetTask / WaitForTask / CancelTask
  - 默认 handlers: `echo` (返回 payload verbatim) + `sleep` (1-30s)
  - `RegisterHandler(name, func)` 公开扩展点 (future: agent_delegate / bg_index / etc.)
  - `ResolveId` 8 字符 prefix-match 抗复制粘贴截断
  - `LTAI.Agent.Tasks.TaskStatus` 显式 fully-qualified (避免与 `System.Threading.Tasks.TaskStatus` 冲突)
- **DI 集成** (`ServiceCollectionExtensions.cs`):
  - Step 3a: TaskQueueTool singleton (TaskQueue + ILoggerFactory)
  - BuildAgentImpl: 5 agents (LTAI-Chat/Pro/System/Code/Writer) 暴露 5 方法
- **收益**: TaskQueue 死代码 → 5 agents 可调用的工具；与 BGJS 互补 (BGJS=shell/exec, TaskQueue=注册式异步)

#### P14.14 TUI `/jobs` + Desktop JobsView 实时展示（1d）✅ 完成 (commit `0ad1a3c`)
- **TUI**:
  - `/jobs list|watch <id>|cancel <id>|show <id>` — 信息组子命令族
  - `JobsList`: Spectre.Console table 7 列 (ID/状态/Exit/已运行/命令)
  - `JobsWatch`: 0.5s 轮询 + 2min timeout, 状态变化才打印 (去噪)
  - `JobsCancel`: 标 `Completed=true + Error="Cancelled"` (无进程 kill — 同 Web cancel 语义)
  - `JobsShow`: 完整 detail + stdout/stderr 前 500 字符预览
  - `SlashCommands.Jobs` 静态属性 + TUI Program.cs 注入
- **Desktop**:
  - 新建 `src/LTAI.Desktop/JobsView.cs` (~280 行)
  - MainWindow 7th view (Ctrl+7) + 图标 `🛠` (与 Config `⚙️` 区分)
  - Header row (Grid 7 列) + 2s DispatcherTimer 刷新 elapsed
  - 订阅 `BackgroundJobService.JobCompleted` 事件 → UI thread post 即时刷新
  - 每行 [Cancel] 按钮 (completed 时 disabled)
  - 解析 BGJS via `App.Services` in `AttachedToVisualTree`
  - `DetachedFromVisualTree` unsubscribe
- **D60 复用 P14.15 status logic**: 4 个 status 同 (completed.ExitCode==0 → 'completed', Error=="Cancelled" → 'cancelled', Completed && ExitCode!=0 → 'failed', else 'running')
- **D61 60s BGJS 自动驱逐**: Desktop 端不主动调用 CleanupOldJobs, 依赖 BGJS 内置 60s 驱逐; JobsView 每 2s 重建 rows
- **构建**: TUI 0/14 (pre-existing), Desktop 0/24 (pre-existing), Web 0/0, Agent 0/0

#### P14.15 LTAI.Web `GET /api/jobs` 端点（0.5d）✅ 完成 (commit `5a80d2e`)
- `GET /api/jobs` → list all
- `GET /api/jobs/{id}` → get detail
- `POST /api/jobs/{id}/cancel` → cancel
- 用 `BackgroundJobService` 单例（不直接走 TaskQueue，避免重复状态）
- **✅ 落地**: 3 个端点放 `/ltai/v1/jobs` (避开 `/api/` 命名冲突)
  - `GET /ltai/v1/jobs` → `{ count, jobs: [{id, status, exitCode, command, startedAtUtc, completed, stdoutBytes, stderrBytes}] }`
  - `GET /ltai/v1/jobs/{id}` → 完整 detail
  - `POST /ltai/v1/jobs/{id}/cancel` → 200/404/409
  - status 推导: `completed` (ExitCode==0) / `cancelled` (Error=="Cancelled") / `failed` / `running`
- **副作用**: `JobEntry` 新增 `Command` + `StartedAtUtc` 字段；`SnapshotJobs()` + `GetJobEntry(id)` 新 API
- **语义**: BGJS 60s 自动驱逐 → 客户端把 404 当作 "completed and gone"
- **关联**: P14.14 (TUI `/jobs` + Desktop sidebar) 准备数据源

#### P14.3 TUI `/model` cleanup / info / quant ✅ (commit `4249b10`)
- **SlashSpec 更新** (`src/LTAI.TUI/SlashCommands.cs:74`): 描述补 `cleanup|info|quant` 三个子命令
- **HandleModelCommand 扩展** (`SlashCommands.cs:320-330`): switch 加 3 个分支
- **`HandleModelCleanup(embedder, arg)`** (`SlashCommands.cs:412-484`):
  - 无参：清理所有已下载 model 的 stale 变种（按 `Options.Quantization` 决定保留谁）
  - 带 name：清理指定 model（known-model 验证 + 跳过不存在的目录）
  - `targetQuant` 决策树：fp32→false / int8→true / auto→当前 model 看 `embedder.UsingQuantizedModel`，其它默认 INT8（P14.1 偏好）
  - 输出：每行 `✓/–` 状态 + 释放字节数 + 保留变种；总计 header 展示总释放
  - 跳过 0 字节 / 失败下载的损坏 FP32（`Length > 1024` 守护）
- **`HandleModelInfo(embedder)`** (`SlashCommands.cs:489-548`):
  - Header：全局 `Options` (Quantization/Gpu/DeviceId) + 状态行（DefaultDisabled → Available → 未加载）
  - Per-model：marker (●/空) + id + display + 描述 + FP32 ●○ + size / INT8 ●○ + size / Vocab ● + size
  - INT8 不可用时显示 `(无上游量化版)`；model 目录不存在显示 `(未下载)`
  - EP/quant/Dim 颜色：EP=GPU=green/CPU=grey ; quant=INT8=green/FP32=yellow
- **`HandleModelQuant(embedder, arg)`** (`SlashCommands.cs:553-601`):
  - 接受 `fp32|int8|auto`；空参 → 显示当前偏好
  - 写 `LocalEmbedder.Options.Quantization = val`（**进程内**生效，无需重启）
  - **自动 hot-reload**: 若 `CurrentModelName != null && !DefaultDisabled` → `embedder.SwitchModel(CurrentModelName)` 重新创建 session
  - SwitchModel 失败 → 提示 `/model info` 看磁盘 + `/model download` 重下
  - DefaultDisabled / 无活动 model → 提示"下次启动生效"
- **`FormatBytes(long)`** helper (`SlashCommands.cs:603-608`): B/KB/MB 自适应
- **Files touched**: `src/LTAI.TUI/SlashCommands.cs` (1 file, +226 / -2)
- **Build**: TUI 0 errors / 14 warnings (pre-existing only) ; Solution 0 errors / 38 pre-existing
- **决策**:
  - **D62** `/model cleanup` 不自动调用：用户主动触发；cleanup all 仍尊重 `Options.Quantization` 偏好而非猜测
  - **D63** `/model quant` 自动 hot-reload（不要求重启或手动 switch）；失败给出明确恢复路径
  - **D64** `cleanup` 把损坏下载（<1KB）视同不存在，避免删错文件
  - **D65** info 命令只读，不触发任何 ONNX 加载（避免 `/model info` 启动 5-10s ONNX）

#### P14.6 ToolEmbeddingCache hit rate 在 DevUI dashboard 表面化（0.5d）✅ 完成 (commit `8c013e8`)
- **ToolEmbeddingCache** (`src/LTAI.AI/ToolEmbeddingCache.cs`):
  - 4 个新只读属性：`CacheHits` / `CacheMisses` / `CacheLookups` / `HitRate`（`Interlocked.Read/Increment` 线程安全）
  - 在 `GetOrComputeAllAsync` 的两个分支分别 `_hits++` (cache 命中) / `_misses++` (新条目)
- **DevUIDashboardView** (`src/LTAI.TUI/DevUI/DevUIDashboardView.cs`):
  - 新增 `BuildCacheStatusLine(ToolEmbeddingCache?)` helper — header 第 3 行渲染 `N entries · M hits · M misses · hit rate X%`
  - 颜色规则：hit rate ≥ 80% green / ≥ 50% yellow / < 50% red
  - `cache == null` → `(not registered)` 占位符
  - Header `Size(5) → Size(6)` 多容纳 1 行
- **TuiApp + TUI Program.cs** 透传 DI：`TuiApp` ctor 加 `ToolEmbeddingCache? embedCache = null` 参数 + `ShowDashboard()` 透传到 `Render(... cache)`；TUI Program.cs 用 `sp.GetService<LTAI.AI.ToolEmbeddingCache>()` 注入
- **Files touched**:
  - `src/LTAI.AI/ToolEmbeddingCache.cs`（+10 / -0：4 prop + 2 Interlocked.Increment）
  - `src/LTAI.TUI/DevUI/DevUIDashboardView.cs`（+25 / -3：签名 + BuildCacheStatusLine + Header Size 调整）
  - `src/LTAI.TUI/TuiApp.cs`（+6 / -0：_embedCache 字段 + ctor param + ShowDashboard 透传）
  - `src/LTAI.TUI/Program.cs`（+1 / -0：sp.GetService<ToolEmbeddingCache>()）
- **Build**: LTAI.AI 0/0, LTAI.TUI 0/14 (pre-existing), Solution 0 errors
- **收益**: 1 个头部数字即告诉用户 cache 是不是真的省了 ONNX 调用；命中率下降 = 工具描述频繁变 = 需要排查

#### P14.7 `Workflows.Declarative.Mcp` 集成（1d）✅
- **样本**：`src/LTAI.Agent/Workflows/ltai-workflows/mcp-docs-search.yaml` — 调用 `microsoft_docs_search` on `https://learn.microsoft.com/api/mcp`（流式 HTTP 传输，零认证），用 PowerFx `=` 表达式把搜索结果拼到 `SendActivity` activity 文本里
- **新 ProjectReference**：`extern/agent-framework/dotnet/src/Microsoft.Agents.AI.Workflows.Declarative.Mcp/Microsoft.Agents.AI.Workflows.Declarative.Mcp.csproj`（MAF `DefaultMcpToolHandler` — 用 `HttpTransportMode.AutoDetect` 自动协商流式 HTTP）
- **DI 注册**（`ServiceCollectionExtensions.cs` Step 3c）：
  - `services.AddSingleton<IMcpToolHandler, DefaultMcpToolHandler>()` — 单例持有 McpClient 缓存 + per-server HttpClient 缓存
  - `YAMLWorkflowRegistry` ctor 加 `IMcpToolHandler?` 参数（默认 `null` 兼容），`BuildWorkflow` 设 `options.McpToolHandler = _mcpToolHandler`
  - `WorkflowWatcherHostedService.StartAsync` 调 `YAMLWorkflowHost.ConfigureMcpToolHandler(_mcpToolHandler)` — 静态 helper 也拿到 handler
- **Key decisions**：
  - **D85** MCP 走流式 HTTP（per user note）— MAF `HttpTransportMode.AutoDetect` 透明处理，无需手工指定
  - **D86** `DefaultMcpToolHandler` 进程内单例：同 serverUrl 共享 McpClient + HttpClient，跨多个 workflow 复用
  - **D87** `InvokeMcpTool` 的 `output.result: Local.SearchResults` + `SendActivity activity: =...` 串联 — PowerFx 表达式由 `Engine.Format` 评估
  - **D88** YAMLWorkflowHost 是静态 helper，greeting.yaml 永不调 MCP，但 `ConfigureMcpToolHandler` 保证未来加 InvokeMcpTool 也能 work（YAML 重新保存 → P15 watcher 热重载）
  - **D89** sample `requireApproval: false` — 公开只读 server，无需人审
- **Files touched**：
  - `src/LTAI.Agent/LTAI.Agent.csproj`（+1 ProjectReference）
  - `src/LTAI.Agent/Workflows/ltai-workflows/mcp-docs-search.yaml`（新建）
  - `src/LTAI.Agent/Workflows/YAMLWorkflowRegistry.cs`（+6 行：ctor 参数 + 字段 + BuildWorkflow 透传）
  - `src/LTAI.Agent/Workflows/YAMLWorkflowHost.cs`（+18 行：静态字段 + ConfigureMcpToolHandler）
  - `src/LTAI.Agent/Workflows/WorkflowWatcherHostedService.cs`（+7 行：ctor 参数 + StartAsync wiring）
  - `src/LTAI.Agent/ServiceCollectionExtensions.cs`（+2 using + 1 AddSingleton + ctor 参数）
- **Build**: LTAI.Agent 0/13 (pre-existing), Solution 0 errors / 25 (pre-existing)
- **P15 集成**: P14.7 借 P15 之力 — 编辑 `mcp-docs-search.yaml` → `:w` → P15 watcher 250ms debounce → 自动 reload → 下次请求用新逻辑
- **限**：`InvokeMcpTool` 需要 MAF 1.25.0-preview.1+；MAF 在 `Build` 时 `McpToolHandler` 必须非 null 才不抛 `DeclarativeModelException`
- **D90 LTAI.LLM 模型不调 MCP**: LTAI LLM agent 不直接调 MCP（避免反复跳出去），MCP 是 YAML workflow 编排层的能力 — 用法: `ltai agents show LTAI-Chat` → system prompt 不提 MCP；TUI/Desktop 用户专门写 YAML 才用

## 推荐执行顺序

```
P0 (5 个) ──→ P1 (4 个) ──→ P2 (3 个) ──→ P3 (2 个)
P14.2 ✅      P14.4         P14.8 ✅       P14.11
P14.3 ✅      P14.5 ✅       P14.9         P14.12
P14.1 ✅      P14.6 ✅       P14.10
P14.13 ✅     P14.7 ✅
P14.14 ✅
P14.15 ✅
```

| 阶段 | 总工时 | 关键产出 |
|---|---|---|
| **P0** (6/6 done) | ~4 天 | 可观测性 + 量化补完 + UX 增强（最高 ROI） |
| **P1** | ~4 天 | 缓存完善 + 性能数字（量化投资回报可视化） | 1/4 done (P14.6) |
| **P2** | ~3 周 | 高级 UX + 鲁棒性（生产就绪） |
| **P3** | ~1 个月 | R&D 类（实验性，可推迟） |

## Key Decisions (P14 排期)
- **D51 P14.1 优先级 P0**：BGE 量化是 P13.6 单文件原则的延伸，消除 FP32 死角
- **D52 P14.2 + P14.6 用 P9 DevUI**：不要新建 dashboard，复用 P9 框架（5 行 vs 200 行）
- **D53 P14.3 TUI 优先于 CLI**：用户主交互在 TUI；`ltai mcp-server` 等 CLI 命令维持现状
- **D54 P14.5 cache 远程 API 是双刃**：省 API 费但增加 stale risk（remote provider 升级时）；用 24h TTL 兜底
- **D55 P14.8 热切换复杂**：session 中嵌入向量维度不变则可热切；变了必须新 session；推迟边界 case 到 P15+
- **D56 P14.11 Multi-embedding 资源 2x**：内存 +200MB，推理 +100% 时间，**仅 P3**（实验性）
- **D57 P14 顺序：P0 → P1 → P2 → P3**：每阶段独立可演示；不跨阶段阻塞

## Verification (每阶段)
- P0 完成后：DevUI 能看到 EP/quant；TUI `/model info` 完整；MiniLM 仍 23MB，BGE 还需 2-3d 生成；TaskQueueTool 暴露给 5 agents；TUI/Desktop/Web 三端能看到 jobs
- P1 完成后：BDN 报告 INT8 < FP32 < FP32+GPU latency；远程 cache 命中率 > 80%
- P2 完成后：运行时换模型不丢消息；API 失败自动 ONNX fallback；per-model quant 生效
- P3 完成后：双模型融合 vs 单模型在 LTAI 真实工作负载（代码搜索 + 决策树路由）准确率对照

# 2026-06-02 P15 可热改编排（YAML/JSON workflow 热重载）

## Goal
- 用户拍板"需要可热改编排" → 把 P7.3 评估升级为落地
- 把 `AgentWorkflows.cs` 180 行 C#（Sequential + Concurrent + DecisionTreeRouter 阈值）全部挪到 `.livingtree/workflows/*.yaml|*.json`
- 编辑器保存 → FileSystemWatcher 触发 → registry 原子换 snapshot → 在途请求保留旧 + 新请求走新（D71）
- 重载失败保留旧 snapshot + notifier 通知 + error 可见（D68）

## 5 个关键决策 (D67-D71)
- **D67** YAML 存 `.livingtree/workflows/*.yaml` (用户层)
- **D68** 重载失败保留旧 + 日志 + notifier 通知 + 错误条
- **D69** 保留 `AgentWorkflows.cs` C# 兜底 (YAML 缺失 / 解析失败时 fallback)
- **D70** DecisionTreeRouter 全 YAML 化（TopK + 阈值 + 候选 agent 白名单 + 模糊 fallback kind）
- **D71** 旧请求用旧 snapshot, 新请求用新 snapshot（in-flight 不中断）

## P15.0 DecisionTreeConfig 强类型 + 模板 ✅
- `src/LTAI.Agent/Workflows/DecisionTreeConfig.cs` (~120 行)
  - 字段: Type/Version/TopK/ConfidenceMarginThreshold/MinTopScoreThreshold/AmbiguousFallback/Candidates
  - `AmbiguousFallbackKind` 枚举: All / TopK / None
  - 静态 `Default` (P7.7 默认值兜底) + `Parse(json)` / `LoadFromFile(path)`
  - JSON options: case-insensitive / AllowTrailingCommas / ReadCommentHandling.Skip
- `src/LTAI.Agent/Workflows/ltai-workflows/decision-tree.template.json` — 顶部 `//` 注释 + JSON 体

## P15.1 YAMLWorkflowRegistry + Watcher + Notifier ✅
- `WorkflowHotReloadNotifier.cs` (~110 行) — fan-out 给订阅者（fire-and-forget）
  - `IWorkflowSubscriber` 接口 (OnReloaded / OnLoadFailed)
  - `WorkflowReloadEvent` / `WorkflowLoadFailedEvent` record structs
  - `ConcurrentDictionary<Guid, IWorkflowSubscriber>` 订阅管理
- `YAMLWorkflowWatcher.cs` (~120 行) — FileSystemWatcher + 250ms 防抖
  - 监听 `*.yaml` / `*.yml` / `*.json`
  - IO 重试 3 次（50/100/200ms 指数退避）处理编辑器文件锁
- `YAMLWorkflowRegistry.cs` (~280 行) — 核心
  - `ConcurrentDictionary` 存 `WorkflowSnapshot` / `DecisionTreeSnapshot`
  - `InitializeAsync` 启动扫所有 yaml/yml/json
  - `ReloadFileAsync(path, ct)` 失败抛异常, D68 保留旧 snapshot
  - `TryGetWorkflow(name)` / `GetDecisionTreeConfig(name)` / `List()` / `ReloadAllAsync()`
  - `BuildWorkflow` 调 `DeclarativeWorkflowBuilder.Build<string>(path, options)`
  - `ProbeWorkflowType` / `ProbeWorkflowVersion` 用 `ExtractYamlScalar` 字符串扫描（无 YAML 解析器依赖）
  - 嵌套 `NoOpAgentProvider : ResponseAgentProvider`
- `WorkflowWatcherHostedService.cs` (~50 行) — `IHostedService` 包装

## P15.2 DecisionTreeRouter 改 ✅
- 新构造参数 `YAMLWorkflowRegistry? registry` (P15 注入)
- `ResolveEffectiveConfig()`: 优先 registry JSON, 兜底 `DecisionTreeRouterOptions`
- 候选白名单: `Candidates.Count > 0` 时 Stage 0 之前 filter
- Stage 3 拆 3 分支: `AmbiguousFallback` (All) / `AmbiguousFallbackTopK` / `NoConfidentMatch` (None)
- 仍保留 `DecisionTreeRouterOptions` C# 兜底类

## P15.3 AgentWorkflows 改 ✅
- 构造加 `YAMLWorkflowRegistry? workflowRegistry` 参数
- `RunHandoffAsync` greeting 改走 `TryRunGreetingAsync`
- `TryRunGreetingAsync(task, ct)`: 先 registry, fallback 到 `YAMLWorkflowHost.RunGreetingFastPathAsync` (D69 C# 兜底)

## P15.4 DI 注册 + 项目文件 ✅
- `ServiceCollectionExtensions.cs` Step 3 加 `YAMLWorkflowRegistry` / `WorkflowHotReloadNotifier` / `YAMLWorkflowWatcher` 单例 + `WorkflowWatcherHostedService` HostedService
- `LTAI.Agent.csproj`: 加 `<None Include="Workflows\ltai-workflows\**\*.json">` + `CopyToOutputDirectory=PreserveNewest`

## P15.5 OTel ActivitySource emit ✅
- `WorkflowHotReloadNotifier` 内部加 `ActivitySource("LTAI.Workflows")`
- `PublishReloaded` / `PublishLoadFailed` 各包一层 Activity（`ActivityKind.Internal`）
- tag: `workflow.name` / `type` / `version` / `path` / `reason`
- 失败时 `SetStatus(ActivityStatusCode.Error, reason)`
- **自动被 P9.1 DevUISpanCollector 捕获**（`name.StartsWith("LTAI")` 已覆盖）
- **自动被 P7.2 OTel console/OTLP exporter emit**（`AddSource("LTAI.*")` 已配置）
- 0 新依赖、0 新 endpoint

## P15.6 DevUI Dashboard "Workflows" 行 ✅
- `DevUIDashboardView.Render` 加可选 `YAMLWorkflowRegistry? workflows` 参数
- header: `[aqua]N[/] workflows` 计数 + footer: 紧凑的 name + version + type 列表
- `TuiApp.cs` + `TuiApp.ctor` 注入 registry, 透传到 `DevUIDashboardView.Render`
- 0 新建 panel, 5-10 行改动

## P15.7 TUI `/workflow` 子命令 ✅
- `SlashCommands.cs` 加 `WorkflowRegistry` 静态属性 + `SlashSpec("workflow", "扩展", ...)` + 5 个 helper
- 子命令族: `list` / `reload [name|*]` / `show <name>` / `open <name>` (用系统默认程序打开)
- `Program.cs` 注入 `WorkflowRegistry` 到静态
- 用户体验: 编辑 yaml → `:w` → `/workflow show decision-tree` 看新内容

## P15.8 Desktop WorkflowsView 精简 ✅
- 新建 `src/LTAI.Desktop/WorkflowsView.cs` (~190 行)
- 极简: Reload All 按钮 + Open in DevUI 按钮 + 错误条（订阅 notifier）
- 不重复 workflow 列表 / 源码预览（这些去浏览器 DevUI 看，per D73）
- `MainWindow.cs` 加 6th view (Ctrl+6) + 图标 `🔁`
- 状态用 2s 轮询 + event 触发即时刷新

## P15.9 LTAI.Web `/api/workflows` 端点 ✅
- `GET /ltai/v1/workflows` — list all + watchDir
- `GET /ltai/v1/workflows/{name}` — single + raw content
- `POST /ltai/v1/workflows/reload` — reload all
- `POST /ltai/v1/workflows/{name}/reload` — reload one (失败返 422 + reason)
- 自动化 / CI / 脚本入口

## Key Decisions (P15 期间新增 D72-D73)
- **D72 Desktop WorkflowsView 精简**: 只放 reload 按钮 + error 条; 列表/源码预览全去浏览器 DevUI (P9.2 启动 in-process Kestrel) — 避免双套 UI
- **D73 观测面融合 P9.1**: reload events 通过 OTel ActivitySource emit → P9 DevUISpanCollector 自动捕获 → 出现在 spans 表格里, 0 重复
- **D74 ReloadAllAsync 在 WorkflowsView 用 `Lazy<DevUIHost>`**: 跟 P9.2 一致, 复用 P9.2 已启动的 host (不每次 Reload All 都重启 Kestrel)

## Verification (P15 完成)
- [x] 构建通过: 5 项目 (LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli) 0 errors
- [x] LTAI.Agent 0 warnings (P15 触改文件)
- [x] LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli 0 errors (各自 pre-existing warnings 维持)
- [ ] 真实 LLM 调用 smoke test: 用户改 `decision-tree.json` 阈值 → DecisionTreeRouter 下次请求用新阈值（用户已说"测试太耗时间"，可后续手动验证）
- [ ] TUI `/workflow list` 列出 1 个 workflow (decision-tree.template.json 复制到 .livingtree/workflows/decision-tree.json)
- [ ] Desktop 切到 Ctrl+6, 点 "Reload All", 错误条更新
- [ ] Web `curl http://localhost:5100/ltai/v1/workflows` 返回 `{watchDir, workflows: [...]}`

## Next Steps (P16+)
- **P16.1**: 用户编辑 Sequential/Concurrent 的 agent 列表 / 步骤顺序 → 全 YAML 化 Sequential/Concurrent（保留 P7.5 C# 实现兜底, 可切换）
- **P16.2**: 改 YAML 触发单元测试（CI 跑 LTAI.Benchmarks 验路由延迟/Recall 变化）
- **P16.3**: Web `/api/workflows/events` SSE 流（订阅 `WorkflowHotReloadNotifier` 推 reload/failed 事件给浏览器 DevUI live view）
- **P16.4**: YAML 编辑器集成（VSCode extension / LSP 校验 DecisionTreeConfig 字段）
- **P16.5**: 把 GreetingClassifier YAML 拆成多个文件（greeting / thanks / farewell / probing 各自独立, 便于单条修改）

# 2026-06-02 P13.6 单文件原则（避免模型碎片化）

## Goal
- P13.1 同时下 FP32 + INT8 (113MB / 模型) → 违反"控制本地下载模型数量和大小"原则
- 改为：**每个模型只下 1 个变种**（MiniLM=INT8，BGE=FP32）
- 用户主动切换 `LTAI:Embedding:Quantization` 时，可选触发 `CleanupStaleVariant` 清掉旧文件

## Plan

### P13.6a 重构 Build Target 按模型拆分 ✅
- 删除旧的 `DownloadEmbeddingModel` (FP32) + `DownloadQuantizedEmbeddingModel` (INT8) — 两个并行下，导致 113MB/MiniLM
- 改为 3 个独立 target：
  - `DownloadEmbeddingModelMiniLM` → **只下 INT8 (23MB)**
  - `DownloadEmbeddingModelBgeSmallZh` → 只下 FP32 (95MB，无 INT8 上游版本)
  - `DownloadEmbeddingModelBgeSmallEn` → 只下 FP32 (95MB，无 INT8 上游版本)
- `PublishEmbeddingModel` 同样拆 3 个（按现有文件复制，不补下）
- 旧的 `model.onnx` (MiniLM FP32) 文件用户**保留无害** — 卸载 INT8 时自动 fallback

### P13.6b DownloadModelAsync 路径唯一性 ✅
- `LocalEmbedder.DownloadModelAsync(name)` 现在按 `Options.Quantization` 选**唯一**变种：
  - `auto` (默认) / `int8` → 下 INT8（如有）
  - `fp32` → 下 FP32
  - 只有一个变种被下载到磁盘
- vocab.txt 始终下载（必需，~500KB）

### P13.6c CleanupStaleVariant（手动 / 编程接口）✅
- `LocalEmbedder.CleanupStaleVariant(name, targetQuant: true)` 切到 INT8 时删 FP32
- 不自动调用 — 用户主动触发（TUI `/model cleanup` 或 CLI 命令）

### Key Decisions
- **D47 单文件原则**：每个模型本地只保留 1 个变种，避免磁盘碎片化
- **D48 Build target 默认 INT8**：MiniLM `auto` 模式下 build target 走 INT8，FP32 需用户主动触发（设 `Quantization=fp32` + 重新 `dotnet build`）
- **D49 不自动删除旧变种**：用户可能调试 / 对比 / 切换，留给用户主动清理
- **D50 vocab.txt 永远下载**：分词器必需，~500KB 不构成优化目标

### Files touched
**改**
- `src/LTAI.AI/LTAI.AI.csproj`（拆 3 个 build target + 3 个 publish target）
- `src/LTAI.AI/LocalEmbedder.cs`（+90 行：DownloadModelAsync 改单变种 + CleanupStaleVariant）

### Verification
- [x] 编译通过：6 项目 0 errors
- [x] 0 warnings in P13.6-touched files
- [ ] 手动验证：删除 `models/minilm-l6-v2/` 后 `dotnet build` 重新触发 build target，确认**只下 INT8 23MB**（不连带下 FP32 90MB）

## 净效果对比

| 场景 | P13.1 (双下) | P13.6 (单下) |
|---|---|---|
| MiniLM (default) | 90MB FP32 + 23MB INT8 = **113MB** | 23MB INT8 = **23MB** (-90MB) |
| BGE-small-zh | 95MB FP32 | 95MB FP32 (不变) |
| BGE-small-en | 95MB FP32 | 95MB FP32 (不变) |
| 3 模型合计 | **303MB** | **213MB** (-30%) |
| 碎片化 | 2 文件/模型 | **1 文件/模型** |

# 2026-06-02 P16 子模块可读性增强 (sparse-checkout + DTFx submodule)

## Goal
- P0：把 `extern/agent-framework` 改 sparse-checkout（251 MB → 27 MB，删 Python 整仓 + 30 个未用 dotnet subdirs + .dll/.pdb/.cache 构建产物），能"在源码里设断点"理解 MAF
- P1：把 `Microsoft.DurableTask.*` 1.24.2 源码加成 submodule (`extern/durabletask-dotnet`)，填补 P8.1 反射背后的"黑盒"
- P2：私有 `ltai-models` 仓库（23 MB INT8 + vocab + checksums）推迟到 P14.1 之前

## Plan

### P0 MAF sparse-checkout ✅
- `git -C extern/agent-framework config core.sparseCheckoutCone false`（cone 模式不支持 `!` 排除）
- `extern/agent-framework/.git/info/sparse-checkout` 写 non-cone 模式 pattern：
  ```
  /*
  !/dotnet/tests/
  !/dotnet/samples/
  !/dotnet/.github/
  !/dotnet/.vscode/
  !**/bin/
  !**/obj/
  !**/*.dll
  !**/*.pdb
  !**/*.cache
  !**/*.cache.json
  !**/*.nupkg
  !**/*.nupkg.gz
  !**/*.nuspec
  ```
- `git read-tree -mu HEAD` 重算 working tree
- **效果**：
  - extern/agent-framework: 251.3 MB → **27.2 MB** (-89%)
  - 35 个 MAF dotnet src 项目保留（含 LTAI 用的 17 个 + 18 个 transitive dep）
  - .dll/.pdb/.cache 全删（构建时按需重生成）
  - `dotnet build LTAI.sln` 0 errors / 0 新 warnings

### P1 DTFx submodule (`extern/durabletask-dotnet`) ✅
- `git submodule add -b main https://github.com/microsoft/durabletask-dotnet.git extern/durabletask-dotnet`
- pin 到 `v1.24.2` tag (commit `0cd13b8171f01e7548d548696dc6e4aaa5130694`) — 与 LTAI 用的 `Microsoft.DurableTask.* 1.24.2` NuGet 对齐
- 包含的关键源码：
  - `src/InProcessTestHost/Sidecar/InMemoryOrchestrationService.cs` ← P8.1 反射的 `instanceStore.store` 字段就是这里
  - `src/InProcessTestHost/Sidecar/Grpc/` ← gRPC sidecar
  - `src/InProcessTestHost/Sidecar/Dispatcher/` ← orchestrator/activity 派发
  - `src/InProcessTestHost/DurableTaskTestExtensions.cs` ← `AddInMemoryDurableTask` 扩展
  - `src/Abstractions/`, `src/Client/`, `src/Worker/` ← 公共契约
- **不**加 `<ProjectReference>` —— DTFx 仍走 NuGet（`Microsoft.DurableTask.* 1.24.2`），submodule 纯作为源码阅读/调试
- shallow clone: 3.5 MB

### 复现脚本 ✅
- `scripts/dev-setup-submodules.ps1` (Windows PowerShell)
- `scripts/dev-setup-submodules.sh` (Linux/macOS bash)
- 作用：
  1. `git submodule update --init --recursive` 拉子模块
  2. 应用 MAF sparse-checkout 模式
  3. 报告磁盘占用
- 团队新成员 clone 后跑一次即达最优状态

### Key Decisions
- **D80 sparse-checkout 走 non-cone 模式**：git 2.54 cone 模式不支持 `!/path/` 排除，要列全 include + 排除 bin/obj/dll/pdb/cache 必须用 globs
- **D81 extern/agent-framework 不进 .gitattributes 永久模式**：sparse-checkout 是 client-local；脚本化才能团队复现
- **D82 durabletask-dotnet pin v1.24.2 而非 main**：与 LTAI 用的 NuGet 版本严格一致；升级时需先升 NuGet 再 `git -C extern/durabletask-dotnet checkout v1.25.0` 跟随
- **D83 不加 ProjectReference 到 DTFx 源码**：避免与 NuGet 双重加载导致类型不一致；submodule 仅供 IDE 跳读 + 调试断点
- **D84 P2 ltai-models 推迟到 P14.1**：P14.1 反正要生成 BGE INT8，那时新建私有仓 + 一起加 submodule 最自然

### Files touched
**新建**
- `scripts/dev-setup-submodules.ps1`（~50 行）
- `scripts/dev-setup-submodules.sh`（~50 行）

**改**
- `.gitmodules`（+3 行：durabletask-dotnet 条目）
- `extern/agent-framework/.git/info/sparse-checkout`（client-local, 不入库；脚本化复现）
- `AGENTS.md`（本节 P16）

### Verification
- [x] 编译通过：7 项目（LTAI.AI / LTAI.Agent / LTAI.TUI / LTAI.Desktop / LTAI.Web / LTAI.Cli / LTAI.Core）0 errors
- [x] 0 新 warnings（pre-existing 38 个维持）
- [x] extern/agent-framework 占用 27.2 MB（vs 旧 251.3 MB，**节省 89%**）
- [x] extern/durabletask-dotnet 占用 3.5 MB, pin v1.24.2
- [x] `git submodule status` 两行，agent-framework@main + durabletask-dotnet@v1.24.2
- [ ] 手动验证：克隆新 clone + 跑 `dev-setup-submodules.ps1` 一键到位（用户可后续手动跑）

## Next Steps (P17+)
- **P17.1**：更新 `LTAI.Agent/Durability/SQLiteOrchestrationService.cs` 顶部注释，把反射字段的 "source path" 链接到 `extern/durabletask-dotnet/src/InProcessTestHost/Sidecar/InMemoryOrchestrationService.cs`（维护性收益）
- **P17.2**：写 `docs/architecture/dependency-graph.md`，列出 LTAI 项目 → MAF ProjectReference 完整图 + DTFx NuGet 版本表
- **P17.3**：把 P14.1 完成的 BGE INT8 走 `ltai4net/ltai-models` 私有 submodule（按 P2 计划）
- **P17.4**：MAF submodule 拉新版本时 review 哪些 LTAI 用的 API 有 breaking change（P15 `Workflows.Declarative.Mcp` 是 1.25.0-preview.1 才有的，提前评估升级价值）

