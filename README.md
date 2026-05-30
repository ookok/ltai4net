# LTAI — LivingTree AI

轻量级 AI 编程助手。基于 Microsoft Agent Framework (MAF) 1.8.0，C# .NET 10.0。

## 架构总览

LTAI 采用 **6 层 Agent OS 架构**，自底向上：

```
L5  Agent Layer      — ChatAgent + 9 specialist agents (code/math/data/system/llm/writer/frontend)
L4  Orchestration    — WorkflowOrchestrator + Vector Router + PlanTools + Reflection Loop
L3  Tool System      — 27 tool classes / 15 domains + ToolCallRepairer + Permission Matrix
L2  Memory & KG      — KbGraph / CgGraph (SQLite+FTS5+CTE) + Reranker
L1  LLM & Safety     — MultiProviderChatClient (22 providers) + Safety Guardrails + EmbeddingClient
L0  Runtime          — MAF Pipeline + Wasmtime Sandbox + Budget/Usage Tracker + OpenTelemetry
```

📐 [完整架构图](docs/architecture.md)

## 快速开始

```bash
# 1. 设置 API Key
set DEEPSEEK_API_KEY=your_key_here

# 2. 启动（选一种）
dotnet run --project src/LTAI.TUI           # 终端 UI（推荐）
dotnet run --project src/LTAI.Desktop       # 桌面 UI (Avalonia)
dotnet run --project src/LTAI.Web           # Web API

# 3. 运行测试
dotnet test
```

## 发布

```bash
# 一键发布 4 个入口
publish.cmd

# 产物
dist/CLI/     — LTAI.Cli.exe (AOT 原生)
dist/TUI/     — LTAI.TUI.exe (终端 UI)
dist/Desktop/ — LTAI.Desktop.exe (Avalonia 桌面)
dist/Web/     — LTAI.Web.dll (Web API)
```

## Docker 部署

```bash
docker compose up -d
# → http://localhost:5100/swagger
# → POST http://localhost:5100/api/chat  {"message":"hello"}
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
---
```

10 个预置 Agent 覆盖：chat / chat-pro / code / math / data / system / llm / writer / frontend / sql-agent。新增 Agent 只需添加 `.agent.md` 文件，向量路由器自动识别（Agent > 5 时只注入 Top-5 到 routing prompt）。

## 内置 Skills

| Skill | 说明 |
|-------|------|
| `arch-diagram` | 生成项目架构图（Mermaid + SVG） |
| `api-design` | API 设计评审 |
| `architecture-review` | 架构审查 |
| `code-review` | 代码审查 |
| `competitive-analysis` | 竞品分析对比 |
| `data-analysis` | 数据分析 |
| `doc-generator` | 文档生成 |
| `error-handling` | 异常处理审查 |
| `git-workflow` | Git 工作流 |
| `migration-plan` | 迁移计划 |
| `performance-profile` | 性能分析 |
| `refactor-plan` | 重构计划 |
| `security-audit` | 安全审计 |
| `test-writer` | 测试编写 |
| `ui-design` | UI 设计 |
| ... | 共 24 个 |

## 关键特性

| 特性 | 实现 |
|------|------|
| **多 LLM 提供商** | 22 家（中国 9 + 国际 13），自动退化链 + 熔断 |
| **L1→L2 升级** | Flash 模型输出 `<<<NEEDS_PRO>>>` 自动切 Pro |
| **向量路由** | Agent >5 时语义选择 Top-5，prompt 不膨胀 |
| **记忆系统** | SQLite 知识图谱 (FTS5+CTE) + 向量检索 (384d BGE) |
| **代码理解** | TreeSitter AST 索引 + 语义搜索 |
| **安全防护** | 双层 Guardrail（输入 SafetyCoordinator + 输出 SafeChatClient）|
| **沙箱执行** | Wasmtime v44（WASI capability 限制）|
| **成本追踪** | 全链路 token/cost 追踪，支持多用户预算隔离 |
| **可观测性** | OpenTelemetry 追踪 + 指标 |
| **会话持久化** | 自动保存到 `.livingtree/sessions/`，重启恢复 |
| **工具修复** | 自动 JSON 修复、循环检测、模糊匹配 |
| **异常恢复** | 子 Agent 失败自动重试 + 默认 Agent 回退 + 3 次熔断 |

## Web API

| 端点 | 方法 | 说明 |
|------|------|------|
| `/health` | GET | 健康检查（含 KgStore + LLM 提供商状态） |
| `/api/chat` | POST | 非流式聊天（60s 超时） |
| `/api/chat/stream` | GET | SSE 流式聊天（300s 超时） |
| `/swagger` | GET | Swagger UI |

生产加固：API Key 认证 / 速率限制 (60 req/min) / 全局异常中间件 / 请求日志 / CORS 白名单。

## 项目结构

```
├── agents/                     # Agent 声明文件 (.agent.md)
├── skills/                     # 24 个可复用 Skill 脚本
├── src/
│   ├── LTAI.Agent/             # Agent 层 + 工具系统
│   │   ├── Agents/             # ChatAgent
│   │   ├── Tools/              # 27 个工具类
│   │   ├── Vector/             # KgStore / KbGraph / CgGraph
│   │   └── Workflows/          # WorkflowOrchestrator
│   ├── LTAI.AI/                # LLM 路由、嵌入、评估
│   ├── LTAI.Core/              # 内核：配置、安全、路径
│   ├── LTAI.Cli/               # CLI 入口
│   ├── LTAI.TUI/               # 终端 UI (Spectre.Console)
│   ├── LTAI.Desktop/           # 桌面 UI (Avalonia)
│   └── LTAI.Web/               # Web API (ASP.NET Core)
├── docs/                       # 架构文档
├── tests/                      # 28 个单元测试
├── Dockerfile                  # 多阶段 Docker 构建
├── docker-compose.yml          # Docker 编排
└── publish.cmd                 # 一键发布脚本
```

## Commands

```bash
publish.cmd                     # 一键发布 4 入口
dotnet build                    # 编译
dotnet test                     # 运行 28 个单元测试
dotnet run --project src/LTAI.TUI     # TUI 模式
dotnet run --project src/LTAI.Desktop # 桌面模式
dotnet run --project src/LTAI.Web     # Web API (端口 5100)
docker compose up -d            # Docker 部署
```

## Tech Stack

- **C# / .NET 10.0** — 17 个项目
- **Microsoft Agent Framework 1.8.0** — Agent pipeline
- **Wasmtime 44.0.0** — 沙箱执行
- **SQLite + FTS5** — 知识图谱存储
- **ONNX Runtime + BGE** — 本地嵌入
- **Spectre.Console** — TUI
- **Avalonia UI** — 桌面 UI
- **ASP.NET Core** — Web API
- **OpenTelemetry** — 可观测性
- **Serilog** — 结构化日志
- **Docker** — 容器化部署
