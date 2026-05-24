# Microsoft.Agents.AI Framework — ltai4net 能力映射

> 对应仓库: https://github.com/microsoft/agent-framework  
> 当前版本: `1.6.2` (stable) / `1.6.1-preview` (preview)  
> 最后同步: 2026-05-24

## 包引用

| 包 | 版本 | ltai4net 项目 |
|---|---|---|
| `Microsoft.Agents.AI` | 1.6.2 | `LTAI.Agent`, `LTAI.Tools` |
| `Microsoft.Agents.AI.Abstractions` | 1.6.2 | `LTAI.Agent` |
| `Microsoft.Agents.AI.Workflows` | 1.6.2 | `LTAI.Agent` |
| `Microsoft.Agents.AI.Hosting` | 1.6.1-preview | `LTAI.Agent` |
| `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` | 1.6.1-preview | `LTAI.Agent` |
| `Microsoft.Agents.AI.Harness` | 1.6.1-preview | `LTAI.Agent` |
| `Microsoft.Agents.AI.Hyperlight` | 1.6.1-preview | `LTAI.Agent` |

---

## 一、AIAgent 基类

| 框架能力 | ltai4net 使用 | 文件 |
|---|---|---|
| `AIAgent` 抽象基类 | `BaseAgent` 继承链 (ChatAgent/CodeAgent/ReasoningAgent/EIAAgent) | `Agents/BaseAgent.cs:46` |
| `AIAgent.RunCoreAsync()` | 所有 Agent 的核心推理入口 | `BaseAgent.cs:51` |
| `AIAgent.RunCoreStreamingAsync()` | 流式输出入口 | `BaseAgent.cs:115` |
| `AgentSession` | `LTAIAgentSession` 自定义会话 (聊天历史/意图/轮次/压缩) | `MAF/LTAIAgentSession.cs:8` |
| `AgentResponse` | 所有非流式回答 | 全项目 30+ 处 |
| `AgentResponseUpdate` | 流式 token 输出 | 全项目 20+ 处 |
| `AgentRunOptions` | 工作流模式切换 (AdditionalProperties) | `BaseAgent.cs:78,110,131` |

---

## 二、Agent Builder / 中间件

| 框架能力 | ltai4net 使用 | 文件 |
|---|---|---|
| `AIAgentBuilder` | `WithLTAIGovernance()` / `WithToolGovernance()` 扩展 | `MAF/MAFMiddleware.cs:14` |
| `AIAgentBuilder.Use()` | 自定义 governance 中间件注册 | `MAF/MAFMiddleware.cs:17` |
| `AIAgentBuilder.UseLogging()` | ULogging 接入 | `MAF/ServiceCollectionExtensions.cs:26` |
| `AIAgentBuilder.UseOpenTelemetry()` | OpenTelemetry 接入 | `MAF/ServiceCollectionExtensions.cs:27` |

### 自定义中间件

| 中间件 | 功能 | 文件 |
|---|---|---|
| `WithLTAIGovernance` | 意图分类 + 情绪检测 | `MAF/MAFMiddleware.cs` |
| `WithToolGovernance` | 工具调用安全审计 + 沙箱隔离 | `MAF/LTAIFunctionMiddleware.cs` |
| `BudgetTrackingMiddleware` | 每日 token/费用预算控制 | `Middleware/BudgetTrackingMiddleware.cs` |
| `UnifiedSafetyGate` | 输入/输出安全审查 | `AgentFactory.cs:77` |

---

## 三、Workflow 工作流

| 框架能力 | ltai4net 使用 | 文件 |
|---|---|---|
| `Executor` | `PreProcessExecutor` / `PipelineExecutor` | `MAF/GovernorWorkflow.cs:33,83` |
| `ProtocolBuilder` | `ConfigureProtocol()` | `MAF/GovernorWorkflow.cs:35` |
| `[MessageHandler]` | Executor 消息处理方法 | `MAF/GovernorWorkflow.cs:37,87` |
| `IWorkflowContext` | 事件发送 + 流式产出 | `MAF/GovernorWorkflow.cs:38` |
| `WorkflowBuilder` | DAG 编排 `.AddEdge().Build()` | `MAF/GovernorWorkflow.cs:119` |
| `InProcessExecution.RunStreamingAsync()` | 流式执行工作流 | `MAF/GovernorWorkflow.cs:135` |
| `WorkflowEvent` / `WorkflowOutputEvent` / `WorkflowErrorEvent` | 管道事件处理 | `MAF/GovernorWorkflow.cs:108-160` |

### 业务级工作流 (基于 AIAgent.RunAsync，非 Workflow 类型)

| 工作流 | 功能 | 文件 |
|---|---|---|
| `AgentParliament` | K-agent 多智能体辩论投票 | `Workflows/AgentParliament.cs` |
| `SentientParliament` | 三阶段审议 (primary→critic→oracle) | `Workflows/SentientParliament.cs` |
| `PlannerCriticWorkflow` | Planner→Executor→Critic 反思循环 | `Workflows/PlannerCriticWorkflow.cs` |
| `UniversalOrchestrator` | 多 Agent 联邦调度 | `Workflows/UniversalOrchestrator.cs` |

---

## 四、A2A (Agent-to-Agent) 协议

| 框架能力 | ltai4net 使用 | 文件 |
|---|---|---|
| `AddA2AServer()` | A2A 服务端注册 | `MAF/ServiceCollectionExtensions.cs:48` |
| Bearer Auth | `A2A_BEARER_TOKEN` 自动生成 | `Host/Program.cs:162` |
| A2A → P2P 桥接 | `A2aP2pBridge` | `Infra/Network/Bridge/A2aP2pBridge.cs` |

---

## 五、Harness 测试框架

| 框架能力 | ltai4net 使用 | 文件 |
|---|---|---|
| (包已引用，未代码集成) | - | `.csproj` Line 16 |

### 自建 Harness 体系

| 组件 | 功能 | 文件 |
|---|---|---|
| `HarnessSnapshot` | Agent 快照/恢复 | `MAF/Evolution/HarnessSnapshot.cs` |
| `ExperienceDebugger` | 失败模式分析 | `MAF/Evolution/ExperienceDebugger.cs` |
| `HarnessEvolutionEngine` | 组件进化引擎 | `MAF/Evolution/HarnessEvolutionEngine.cs` |
| `ToolsHarnessComponent` | 工具适配度评估 | `MAF/Evolution/ToolsHarnessComponent.cs` |

---

## 六、Hyperlight 沙箱

| 框架能力 | ltai4net 使用 | 文件 |
|---|---|---|
| `HyperlightCodeActProvider` | 微 VM 代码执行提供者 | `CodeAct/CodeActProvider.cs:30` |
| `HyperlightExecuteCodeFunction` | 单函数执行 | `CodeAct/CodeActProvider.cs:37` |
| CodeAct 工具注册 | `codeact_exec` 工具 | `MAF/ServiceCollectionExtensions.cs:90` |

---

## 七、YAML Agent 配置

| 框架能力 | ltai4net 使用 | 文件 |
|---|---|---|
| (自建 YAML 解析) | `AddLTAIAgentsFromYaml()` | `ServiceCollectionExtensions.cs:46-58` |
| `AgentConfig` | 从 YAML 字符串解析 Agent 定义 | `ServiceCollectionExtensions.cs:137-245` |

---

## 八、升级检查清单

每次升级 `Microsoft.Agents.AI*` 包版本后，对照检查：

- [ ] `AIAgent.RunCoreAsync` 签名是否变化
- [ ] `AIAgentBuilder.Use()` 回调签名是否变化  
- [ ] `Executor` / `ProtocolBuilder` / `IWorkflowContext` API 是否 breaking
- [ ] `AddA2AServer()` 注册方式是否变化
- [ ] `HyperlightCodeActProvider` 构造/使用方式是否变化
- [ ] `AIFunctionFactory.Create` 重载是否增加/删除
- [ ] `ChatCompletionOptions.Tools` 传递路径是否完整 (见 `OpenAIProviderChatClient.ToOpenAIOptions`)
- [ ] CI 兼容性门禁是否通过 (`agent-framework-compat.yml`)
