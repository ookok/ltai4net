# LTAI — LivingTree AI

轻量级 AI 编程助手。基于 Microsoft Agent Framework (MAF) 1.8.0，C# .NET 10.0。

## 架构总览

LTAI 采用 **6 层 Agent OS 架构**，自底向上：

```
L5  Agent Layer      — ChatAgent + 19 specialist agents (code/math/data/system/llm/writer/frontend/plan/…)
L4  Orchestration    — WorkflowOrchestrator + Vector Router + PlanTools + Reflection Loop
L3  Tool System      — ToolSet (80+ tools, 15 domains) + Permission Matrix + AgentToolStore
L2  Memory & KG      — KbGraph / CgGraph (SQLite+FTS5+CTE) + Reranker + 7-layer Memory Palace
L1  LLM & Routing    — ProviderRegistry (8 providers) + ModelAutoSelector (L1/L2/L3) + MultiProviderChatClient
L0  Runtime          — MAF Pipeline + Wasmtime Sandbox + Budget/Usage Tracker + OpenTelemetry
```

关键变化：
- **ProviderRegistry**: models.dev 驱动，8 个 provider 自动发现，500+ 模型能力已知
- **ModelAutoSelector**: L1 用户配，L2/L3 自动选拔（综合评分：能力+成本+速度+可用性）
- **ToolSet**: 字典保障工具名唯一，三级优先级（Core > Domain > External），MCP 工具无法覆盖 LTAI 原生工具
- **PseudoTerminal**: 跨平台 PTY（Windows ConPTY + Linux/macOS forkpty）

📐 [完整架构图](docs/architecture.md)

## 快速开始

```bash
# 1. 创建 .env 文件（从模板复制）
copy .env.example .env
# 或:  cp .env.example .env

# 2. 编辑 .env，填入你的 DeepSeek API Key（或任意 LLM Key）
#    DEEPSEEK_API_KEY=sk-your-key

# 3. 启动（选一种）
./run-tui.bat                              # 终端 UI（推荐）
./run-web.bat                              # Web API → http://localhost:5100
./run-desktop.bat                          # 桌面 UI
dotnet run --project src/LTAI.Cli -- health # CLI 健康检查
```

**只需一个 API Key，无需额外配置。** `.env` 文件在启动时自动加载，`appsettings.json` 已内置默认 `deepseek-fast` 提供程序。L1/L2/L3 模型自动选拔。

## 模型自动选拔

用户只需配置一个 API Key，LTAI 自动完成模型选拔：

```
启动: DEEPSEEK_API_KEY=sk-xxx
  → ProviderRegistry 加载 models-dev-providers.json (8 providers, 560+ models)
  → ModelAutoSelector 对 L1/L2/L3 分别评分
      L1: deepseek-chat     ← 快速响应 (tool_call✓, 速度优先)
      L2: deepseek-reasoner ← 深度推理 (tool_call✓, 能力优先)
      L3: deepseek-chat     ← 评判模型 (复用 L1，成本优先)
  → MultiProviderChatClient 注册 L1/L2/L3 IChatClient
  → 后台每 24h 从 models.dev 刷新
```

CLI 查看/覆盖：
```bash
ltai models show                  # 查看当前选拔结果
ltai models set l2 gpt-4o-mini   # 覆盖 L2 模型
ltai models auto l2               # 恢复自动选拔
```

## Agent 配置

Agent 通过 `agents/*.agent.md` 文件声明式配置：

```yaml
---
name: LTAI-Code
description: 代码分析助手
temperature: 0.3
topP: 0.95
permissions: [read, write, list]
tools: [filesystem, search, symbols, git, plan]
modelId: l1
---
```

19 个预置 Agent，新增只需添加 `.agent.md` 文件。

## 内置 Skills

| Skill | 说明 |
|-------|------|
| `arch-diagram` | 生成项目架构图（Mermaid + SVG） |
| `code-review` | 代码审查 |
| `api-design` | API 设计评审 |
| `security-audit` | 安全审计 |
| `refactor-plan` | 重构计划 |
| `test-writer` | 测试编写 |
| `data-analysis` | 数据分析 |
| ... | 共 24 个 |

## 关键特性

| 特性 | 实现 |
|------|------|
| **多 LLM 提供商** | 8 家（models.dev 驱动），自动选拔 L1/L2/L3 + 退化链 + 熔断 |
| **模型自动选拔** | ProviderRegistry (500+ 模型) → ScoringEngine (能力/成本/速度/可用性) |
| **L1→L2 升级** | 质量评分不达标时自动升级深度模型，支持 span 级精准修复 |
| **向量路由** | Agent >5 时语义选择 Top-5，prompt 不膨胀 |
| **工具系统** | ToolSet 字典保障唯一，三级优先级，MCP 工具无法覆盖 LTAI 原生工具 |
| **记忆系统** | SQLite 知识图谱 (FTS5+CTE) + 向量检索 (384d BGE) + 7 层记忆宫殿 |
| **代码理解** | TreeSitter AST 索引 + 语义搜索 |
| **安全防护** | 双层 Guardrail（输入 SafetyCoordinator + 输出 SafeChatClient）|
| **沙箱执行** | Wasmtime v44（WASI capability 限制）|
| **跨平台终端** | 伪终端 (Windows ConPTY + Linux/macOS forkpty) |
| **成本追踪** | 全链路 token/cost 追踪，支持多用户预算隔离 |
| **可观测性** | OpenTelemetry 追踪 + 指标 |
| **会话持久化** | 自动保存到 `.livingtree/sessions/`，重启恢复 |
| **工具修复** | 自动 JSON 修复、循环检测、模糊匹配 |
| **异常恢复** | 子 Agent 失败自动重试 + 默认 Agent 回退 + 3 次熔断 |

## Web API

| 端点 | 方法 | 说明 |
|------|------|------|
| `/health` | GET | 健康检查（含 KgStore + LLM 提供商状态） |
| `/ready` | GET | K8s readiness probe |
| `/v1/agents/{name}/responses` | POST | OpenAI Responses API |
| `/v1/agents/{name}/chat/completions` | POST | OpenAI Chat API |
| `/a2a/{name}` | POST | A2A 协议 |
| `/agui/{name}` | GET | AGUI 协议 |
| `/ltai/v1/entities` | GET | 19 agents LTAIAgentCard |
| `/ltai/v1/workflows` | GET | 热改编排配置 |
| `/ltai/v1/workflows/reload` | POST | 重载所有编排 |

## 项目结构

```
├── agents/                     # 19 个 Agent 声明文件 (.agent.md)
├── skills/                     # 24 个可复用 Skill 脚本
├── models/                     # models-dev-providers.json (8 providers, 560+ models)
├── src/
│   ├── LTAI.Core/              # 内核：配置、安全、路径、secret 管理
│   ├── LTAI.AI/                # LLM 路由、embedding、ProviderRegistry、ModelAutoSelector
│   ├── LTAI.Agent/             # Agent 层 + 工具系统 (ToolSet) + 记忆
│   │   ├── Agents/             # Builder, 10+ agents
│   │   ├── Tools/              # 80+ 工具类
│   │   ├── Clients/            # ChatClient 中间件 (ToolFilteringChatClient)
│   │   ├── Vector/             # KgStore / KbGraph / CgGraph
│   │   └── Workflows/          # AgentWorkflows + YAML hot-reload
│   ├── LTAI.Agent.CodeAnalysis/# 代码分析 (TreeSitter, 语义搜索)
│   ├── LTAI.Agent.Database/    # 数据库工具
│   ├── LTAI.Agent.Documents/   # Office 文档工具
│   ├── LTAI.Cli/               # CLI 入口 (ltai models/set/auto)
│   ├── LTAI.TUI/               # 终端 UI (Terminal.Gui)
│   ├── LTAI.Desktop/           # 桌面 UI (Avalonia + PseudoTerminal)
│   └── LTAI.Web/               # Web API (ASP.NET Core, 端口 5100)
├── docs/                       # 架构文档
├── tests/                      # 112+ 测试
├── scripts/                    # 构建 / 安装脚本
├── Dockerfile                  # 多阶段 Docker 构建 (amd64 + arm64)
└── docker-compose.yml          # Docker 编排
```

## Tech Stack

- **C# / .NET 10.0** — 17 个项目
- **Microsoft Agent Framework 1.8.0** — Agent pipeline
- **models.dev** — Provider/Model 元数据来源
- **Wasmtime 44.0.0** — 沙箱执行
- **SQLite + FTS5** — 知识图谱存储
- **ONNX Runtime + BGE** — 本地嵌入
- **Terminal.Gui** — TUI
- **Avalonia UI** — 桌面 UI
- **ASP.NET Core** — Web API
- **OpenTelemetry** — 可观测性
- **Serilog** — 结构化日志
- **Docker** — 容器化部署

## 发布

```bash
publish.cmd                     # 一键发布 4 入口
# 产物：
#   dist/CLI/     — LTAI.Cli.exe (AOT)
#   dist/TUI/     — LTAI.TUI.exe
#   dist/Desktop/ — LTAI.Desktop.exe
#   dist/Web/     — LTAI.Web.dll
```

## Docker 部署

```bash
docker compose up -d
# → http://localhost:5100
```
