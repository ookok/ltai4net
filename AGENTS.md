# LTAI 4 Net — Agent 指南

多 Agent 框架，基于 Microsoft Agent Framework (MAF)。3 种前端 (TUI/Desktop/Web) + CLI，10 个 agent（由 `agents/*.agent.md` 定义），本地 ONNX 嵌入，YAML 热改编排。

## 项目结构

- `src/LTAI.Core/` — 配置、安全、用量追踪（零外部依赖）
- `src/LTAI.AI/` — LLM 路由器 (`MultiProviderChatClient`)、嵌入 (`LocalEmbedder`)、ToolRegistry
- `src/LTAI.Agent/` — agent 构建、编排、上下文、DevUI 服务、持久化
- `src/LTAI.TUI/` — Spectre.Console 终端 UI
- `src/LTAI.Desktop/` — Avalonia 桌面 UI
- `src/LTAI.Web/` — ASP.NET Minimal API (端口 5100)
- `src/LTAI.Cli/` — CLI 工具 (`ltai`)
- `src/LTAI.Accelerator/` — 独立加速器（非核心 agent 链）
- `src/LTAI.Hpo/` — HPO 扩展
- `src/LTAI.Agent.Eia/` — EIA 扩展
- `extern/agent-framework/` — MAF git 子模块 (Microsoft.Agents.AI)
- `extern/durabletask-dotnet/` — DTFx git 子模块 (源码参考)

## DI 注册顺序（必须保持）

```csharp
services.AddLTAICore();     // 配置、安全、日志
services.AddLTAIAI();       // LLM 路由器、嵌入
services.AddLTAIAgent();    // 10 agents、编排、工具
```

每个 agent 通过 `ServiceCollectionExtensions.GetAgentDefinitions()` 读取 `agents/*.agent.md` 注册为 MAF keyed service。

## Agent 定义

Agent 由 `agents/*.agent.md` YAML front-matter 声明。10 个 agents：`LTAI-Chat`、`LTAI-Chat-Pro`、`LTAI-Code`、`LTAI-Data`、`LTAI-Frontend`、`LTAI-LLM`、`LTAI-Math`、`LTAI-System`、`LTAI-Writer`、`sql-agent`。

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
dotnet build LTAI.sln                     # 构建所有项目（含 MAF 子模块）
dotnet build src/LTAI.TUI                # 仅 TUI
dotnet build src/LTAI.Desktop            # 仅 Desktop
dotnet build src/LTAI.Web                # 仅 Web
./scripts/build-maf.ps1                  # 预编译 MAF 到 dist/lib/maf（加速增量构建）
./scripts/dev-setup-submodules.ps1       # 初始化子模块 + sparse-checkout
cd src/LTAI.TUI && dotnet run            # 启动 TUI
cd src/LTAI.Desktop && dotnet run        # 启动 Desktop
cd src/LTAI.Web && dotnet run            # 启动 Web → http://localhost:5100
dotnet test tests/LTAI.Tests             # 运行测试（112+ 测试）
dotnet run -c Release --project tests/LTAI.Benchmarks  # BenchmarkDotNet
dotnet run --project tests/LTAI.Benchmarks -- smoke    # 快速 smoke test
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
| `GET /ltai/v1/entities` | 10 agents LTAIAgentCard |
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
- **配置**：`appsettings.json` `LTAI` 节 + 环境变量（DEEPSEEK_API_KEY 等）。

## 参考文档

- `docs/architecture.md` — 六层架构图
- `docs/ops/runbook.md` — 操作手册
- `docs/maf-paradigm-evaluation.md` — MAF 范式评估
