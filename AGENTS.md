# LTAI 4 Net — Agent 指南

多 Agent 框架，基于 Microsoft Agent Framework (MAF)。3 种前端 (TUI/Desktop/Web) + CLI，19 个 agent（由 `agents/*.agent.md` 定义），本地 ONNX 嵌入，YAML 热改编排。

## 项目结构

- `src/LTAI.Core/` — 配置、安全、用量追踪（零外部依赖）
- `src/LTAI.AI/` — LLM 路由器 (`MultiProviderChatClient`)、ProviderRegistry、ModelAutoSelector、嵌入 (`LocalEmbedder`)、ToolRegistry
- `src/LTAI.Agent/` — agent 构建、编排、上下文、ToolSet、AgentToolStore、DevUI 服务、持久化
- `src/LTAI.Agent.CodeAnalysis/` — 代码分析（TreeSitter 解析器，语义代码搜索）
- `src/LTAI.Agent.Database/` — 数据库工具
- `src/LTAI.Agent.Documents/` — Office 文档工具
- `src/LTAI.TUI/` — Terminal.Gui 终端 UI (Inline 模式，类 Claude Code/Copilot CLI)
- `src/LTAI.Desktop/` — Avalonia 桌面 UI（内嵌 PseudoTerminal: ConPTY/forkpty）
- `src/LTAI.Web/` — ASP.NET Minimal API (端口 5100)
- `src/LTAI.Cli/` — CLI 工具 (`ltai`)
- `src/LTAI.Accelerator/` — 独立加速器（非核心 agent 链）
- `src/LTAI.Hpo/` — 超参优化引擎（Samplers, Pruners，独立项目）
- `src/LTAI.Mm/` — MetaMessage 记忆模块
- `src/LTAI.Agent.Eia/` — EIA 集成
- `src/Shared/Polyfill.cs` — 跨项目 Polyfill
- `extern/agent-framework/` — MAF git 子模块 (Microsoft.Agents.AI)
- `extern/durabletask-dotnet/` — DTFx git 子模块 (源码参考)
- `extern/Terminal.Gui/` — Terminal.Gui git 子模块 (gui-cs, 预编译 DLL 到 `dist/lib/terminal.gui/`)
- `extern/Editor/` — Terminal.Gui.Editor git 子模块 (gui-cs, 预编译 DLL 到 `dist/lib/editor/)`
- `models/` — models-dev-providers.json（8 provider × 560+ 模型元数据缓存）

## DI 注册顺序（必须保持）

```csharp
services.AddLTAICore();     // 配置、安全、日志
services.AddLTAIAI();       // LLM 路由器、嵌入
services.AddLTAIAgent();    // 19 agents、编排、工具
```

每个 agent 通过 `ServiceCollectionExtensions.GetAgentDefinitions()` 读取 `agents/*.agent.md` 注册为 MAF keyed service。ProviderRegistry 和 ModelAutoSelector 在 DI 启动时自动初始化。

## Agent 定义

Agent 由 `agents/*.agent.md` YAML front-matter 声明。19 个 agents：`LTAI-Chat`、`LTAI-Chat-Pro`、`LTAI-Code`、`LTAI-Data`、`LTAI-Frontend`、`LTAI-LLM`、`LTAI-Math`、`LTAI-System`、`LTAI-Writer`、`LTAI-SQL`、`LTAI-API`、`LTAI-Arch`、`LTAI-DCI`、`LTAI-Test`、`LTAI-Review`、`LTAI-Debug`、`LTAI-Security`、`LTAI-DevOps`、`LTAI-Office`。

```bash
ltai agents list          # 一览
ltai agents show <name>   # 详细 prompt + 工具 + 权限
```

## Prompt 架构

系统 prompt 分三层拼装：

```
Layer 0: agents/system-{lang}.prompt.md  ← 公共基础（身份/风格/策略/验证）
Layer 1: AgentPromptBuilder.cs            ← C# fallback + 语言切换
Layer 2: agents/*.agent.md (正文)        ← 领域专属工作流
```

`system-*.prompt.md` v3 包含以下节：

| 节 | 用途 |
|---|---|
| `<identity>` | 角色身份声明 |
| `<tone-style>` | 输出约束（极简、无 preamble、代码引用格式） |
| `<language>` | 双语切换规则 |
| `<task-execution>` | 任务执行流程（TodoWrite 追踪） |
| `<tool-strategy>` | 工具调用优先级（搜索→读取→编辑链） |
| `<proactiveness>` | 主动性与安全边界 |
| `<code-conventions>` | 代码风格与安全约定 |
| `<tool-usage>` | 工具调用格式约束 |
| `<verification>` | 生成后自动语法检查（3 层：QuickParse + RuleEngine + LSP） |
| `<context-management>` | 上下文主动压缩策略 |

### 生成时语法检查

`GrammarCheckStep`（`Pipeline/Steps/GrammarCheckStep.cs`）在 agent tool 执行后自动运行：

1. **第 1 层** QuickParse — Roslyn/TreeSitter AST 解析（<200ms）
2. **第 2 层** RuleEngine — 确定性规则匹配（<300ms）
3. **第 3 层** LSP — 语义诊断（<500ms）

发现语法错误时：
- 注入错误消息到 agent 上下文（`文件:行号:列号` 格式）
- 设置 `GrammarCheckBlocked` 标志，阻断新任务
- `ChatAgent` 自动重试修复（上限 2 次）

## 关键命令

```bash
dotnet build LTAI.sln                     # 构建所有项目（含子模块）
dotnet build src/LTAI.TUI                # 仅 TUI
dotnet build src/LTAI.Desktop            # 仅 Desktop
dotnet build src/LTAI.Web                # 仅 Web
./scripts/build-maf.ps1                  # 预编译 MAF 到 dist/lib/maf（加速增量构建）
./scripts/build-terminalgui.ps1          # 预编译 Terminal.Gui 到 dist/lib/terminal.gui
./scripts/dev-setup-submodules.ps1       # 初始化子模块 + sparse-checkout
cd src/LTAI.TUI && dotnet run            # 启动 TUI (Inline 模式，需先 build-terminalgui.ps1)
cd src/LTAI.Desktop && dotnet run        # 启动 Desktop
cd src/LTAI.Web && dotnet run            # 启动 Web → http://localhost:5100
dotnet test tests/LTAI.Tests             # 运行测试（112+ 测试）
dotnet run -c Release --project tests/LTAI.Benchmarks  # BenchmarkDotNet
dotnet run --project tests/LTAI.Benchmarks -- smoke    # 快速 smoke test
ltai models show                         # 查看自动选拔的 L1/L2/L3 模型
ltai models set l2 deepseek-chat        # 覆盖 L2 模型
ltai models auto l2                     # 恢复自动选拔
```

## 子模块 & sparse-checkout

首次克隆后必须跑：

```bash
./scripts/dev-setup-submodules.ps1
```

这会把 `extern/agent-framework` 从 251MB 缩到 ~27MB（排除 Python/tests/bin/obj/.dll/.pdb/.cache）。`extern/durabletask-dotnet` 当前 HEAD = `b7216672` (v1.16.2-141, 仅源码参考，不走 ProjectReference)。

> **P0:** 两个 submodule 都跟随 main 分支 — 强烈建议在 `extern/agent-framework` 和 `extern/durabletask-dotnet` 内执行 `git checkout <commit-sha>` 锁版本,避免 `git submodule update` 拉到不兼容 commit。MAF DLL 已预编译到 `dist/lib/maf/`,可通过 `scripts/build-maf.ps1` 重建。

## ONNX 嵌入模型

3 个 Xenova 预量化模型，走 `hf-mirror.com` 镜像：

| 模型 | 默认变种 | 大小 |
|---|---|---|
| MiniLM-L6-v2 | INT8 | 22MB |
| BGE-small-zh | INT8 | 23MB |
| BGE-small-en | INT8 | 32MB |

```bash
dotnet build -t:DownloadEmbeddingModelMiniLM     # 只下 MiniLM INT8
dotnet build -t:DownloadEmbeddingModelBgeSmallZh
dotnet build -t:DownloadEmbeddingModelBgeSmallEn
```

已配远程 API key（DEEPSEEK/OPENAI/SILICONFLOW/DASHSCOPE）时自动跳过 ONNX 加载。GPU 自适应：`LTAI:Embedding:Gpu=auto` 按 DML → CUDA → CPU 探测。

## YAML 热改编排

`.livingtree/workflows/*.yaml|*.json` 可热编辑，保存后 250ms 自动重载（FileSystemWatcher）。支持的编排类型：
- `greeting` — 问候快速通道
- `decision-tree` — 向量路由阈值
- `sequential` / `concurrent` — 管道
- `mcp` — MCP 工具调用（YAML 中 `InvokeMcpTool`）

```bash
TUI: /workflow list | /workflow reload | /workflow show <name>
Web: GET /ltai/v1/workflows
```

## Web 端点

| 端点 | 说明 |
|---|---|
| `GET /health` | 完整健康检查 |
| `GET /ready` | K8s readiness probe |
| `GET /devui` | MAF DevUI（仅 development） |
| `GET /ltai/v1/entities` | 19 agents LTAIAgentCard |
| `GET /ltai/v1/jobs` | 后台任务列表（60s 自动驱逐） |
| `GET /ltai/v1/workflows` | 热改编排配置 |
| `POST /ltai/v1/workflows/reload` | 重载所有编排 |
| `/v1/agents/{name}/responses` | OpenAI Responses API |
| `/v1/agents/{name}/chat/completions` | OpenAI Chat API |
| `/a2a/{name}` | A2A 协议 |
| `/agui/{name}` | AGUI 协议 |

不注册全局 `/v1/responses` 和 `/v1/chat/completions`（与 per-agent 路由冲突）。

## 重要约束

- **Swashbuckle 在 .NET 10 preview 上 TypeLoadException**：用内置 `AddOpenApi()` + `MapOpenApi()`。
- **MAF DevUI 仅在 `IsDevelopment()` 注册**（暴露 system prompt）。
- **OTel console exporter** 默认开启；OTLP 需配置 `LTAI:Telemetry:OtlpEndpoint`。
- **`ShellEnvironmentProvider` 已完全移除**（Windows .NET 10 上启动 PowerShell 进程卡 60+ 秒）。
- **Pre-existing warnings** ~38 个（OfficeDocumentReader/DocumentTools/SkillEvolutionEngine/KbGraph）—— 不新增即可。
- **持久化目录**：`.livingtree/`（SQLite 知识图谱 + 会话 + 任务队列）。删除可重置所有状态。
- **配置**：`appsettings.json` `LTAI` 节 + 环境变量（DEEPSEEK_API_KEY 等）。仅需配置一个 API Key，L2/L3 自动选拔。
- **Provider 元数据**：`models/models-dev-providers.json`（252KB，首次启动自动加载，后台 24h 刷新）。

## 端侧推理

`models/edge-providers.json` 配置本地推理工具的 provider 元数据，加载后与远程 provider 合并使用。

### 支持的端侧工具

| Provider ID | 工具 | 说明 |
|---|---|---|
| `ollama` | [Ollama](https://ollama.ai) | 本地 LLM 运行时，支持 GGUF 模型 |
| `vllm` | [vLLM](https://github.com/vllm-project/vllm) | 高性能推理引擎，支持 PagedAttention |
| `llamacpp` | [llama.cpp](https://github.com/ggerganov/llama.cpp) | C/C++ 推理，支持 CPU/GPU 混合 |
| `lmstudio` | [LM Studio](https://lmstudio.ai) | 图形化本地模型管理 |
| `koboldcpp` | [KoboldCPP](https://github.com/LostRuins/koboldcpp) | 面向角色扮演的推理前端 |

### 切换端侧 Provider

在 `appsettings.json` 的 `LTAI:AI` 节设置 `DefaultProvider`：

```json
{
  "LTAI": {
    "AI": {
      "DefaultProvider": "ollama",
      "MaxTokens": 8192,
      "Temperature": 0.7
    }
  }
}
```

支持的 `DefaultProvider` 值：`ollama`、`vllm`、`llamacpp`、`lmstudio`、`koboldcpp`。

Provider 默认可断连（不配置 endpoint 也不影响启动），通过 `models/edge-providers.json` 中的 `api` 字段配置端侧服务地址。

### Ollama + Qwen3-8B 示例配置

`models/edge-providers.json` 已内置 Qwen3-8B 等模型。启动 Ollama 后拉取模型：

```bash
ollama pull qwen3:8b
ollama serve  # 默认 http://localhost:11434
```

应用自动使用 Ollama provider，无需进一步配置。也可在 `appsettings.json` 显式指定：

```json
{
  "LTAI": {
    "AI": {
      "DefaultProvider": "ollama",
      "MaxTokens": 8192,
      "Temperature": 0.7
    }
  }
}
```

如需自定义 endpoint，修改 `models/edge-providers.json` 中 `ollama.api` 字段。

## 参考文档

- `docs/architecture.md` — 六层架构图
- `docs/ops/runbook.md` — 操作手册
- `docs/maf-paradigm-evaluation.md` — MAF 范式评估

## 环境变量参考

所有环境变量遵循 `LTAI_<DOMAIN>_<PARAM>` 命名规范，默认值匹配原有硬编码。

### 并发控制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_SHELL_CONCURRENCY` | 8 | SafeShellTool 全局并发上限 |
| `LTAI_WASM_CONCURRENCY` | 6 | WasmtimeSandbox 全局并发上限 |
| `LTAI_MOA_CONCURRENCY` | 6 | MoAWorkflow 编排节流 |
| `LTAI_WORKFLOW_CONCURRENCY` | 6 | AgentWorkflows 编排节流 |
| `LTAI_JOB_MAX_CONCURRENT` | 10 | BackgroundJobService 最大并发作业数 |
| `LTAI_SEARCH_MAX_DOP` | min(CPU,4) | SearchTools 并行搜索度 |
| `LTAI_ISSUE_DETECTOR_MAX_DOP` | 4 | IssueDetectors 并行度 |
| `LTAI_TASK_QUEUE_MAX` | 100000 | TaskQueue 有界队列容量 (0=无界) |

### 超时控制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_SHELL_TIMEOUT_SEC` | 30 | WasmtimeSandbox shell 命令超时 |
| `LTAI_WASM_TIMEOUT_SEC` | 60 | WasmtimeSandbox WASM 执行超时 |
| `LTAI_SCRIPT_TIMEOUT_SEC` | 60 | SkillScriptRunner 脚本超时 |
| `LTAI_JOB_PROCESS_TIMEOUT_SEC` | 300 | BackgroundJobService 进程超时 |
| `LTAI_REGEX_TIMEOUT_MS` | 1000 | FileSystemTools/SearchTools 正则超时 |
| `LTAI_SQLITE_BUSY_MS` | 5000 | KgStore SQLite busy_timeout |
| `LTAI_RETRY_BACKOFF_SEC` | `1,2,4,8,16` | RetryQueueWorker 退避序列（逗号分隔） |

### 资源限制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_TOOL_MAX_OUTPUT_BYTES` | 102400 | WasmtimeSandbox 输出截断上限 |
| `LTAI_JOB_MAX_OUTPUT_CHARS` | 100000 | BackgroundJobService 输出截断上限 |
| `LTAI_JOB_EXPIRATION_SEC` | 60 | BackgroundJobService 作业驱逐时间 |
| `LTAI_SQLITE_MMAP_MB` | 256 | KgStore SQLite mmap_size (MB) |
| `LTAI_WASM_MODULE_CACHE_MAX` | 32 | WasmtimeSandbox 模块缓存上限 |
| `LTAI_HTTP_MAX_CONN` | 6 | LLM HTTP 连接池每服务器最大连接 |
| `LTAI_HTTP_POOL_LIFETIME_MIN` | 10 | LLM HTTP 连接池生命周期 (分钟) |
| `LTAI_WATCHER_BUFFER` | 65536 | FileSystemWatcher 内部缓冲区大小 |

### 缓存与间隔

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_LLM_CACHE_TTL_MIN` | 5 | MultiProviderChatClient LLM 响应缓存 TTL |
| `LTAI_COMPRESSION_MAX_AGE_DAYS` | 30 | CompressionStore 条目最大保留天数 |
| `LTAI_CG_CACHE_SIZE` | 100 | CgGraph 查询缓存条目数 |
| `LTAI_CG_CACHE_TTL_SEC` | 30 | CgGraph 查询缓存 TTL |
| `LTAI_MEMORY_CONSOLIDATION_MINUTES` | 30 | MemoryConsolidationService 执行间隔 |
| `LTAI_RATE_LIMIT_CLEANUP_MIN` | 5 | RateLimitMiddleware 清理间隔 |

### 行为控制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_GREETING_MAX_LENGTH` | 15 | QueryClassifier 问候判定最大字符数 |
