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
# 设置 API Key（DeepSeek / SiliconFlow / OpenAI 等）
set DEEPSEEK_API_KEY=your_key_here

# 启动 TUI
dotnet run --project src/LTAI.TUI

# 或启动桌面版 (Avalonia UI)
dotnet run --project src/LTAI.Desktop

# 运行测试
dotnet test
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

9 个预置 Agent 覆盖：chat / code / math / data / system / llm / writer / frontend。新增 Agent 只需添加 `.agent.md` 文件，向量路由器自动识别。

## 关键特性

| 特性 | 实现 |
|------|------|
| **多 LLM 提供商** | 22 家（中国 9 + 国际 13），自动退化链 + 熔断 |
| **L1→L2 升级** | Flash 模型输出 `<<<NEEDS_PRO>>>` 自动切 Pro |
| **记忆系统** | SQLite 知识图谱 (FTS5+CTE) + 向量检索 (384d BGE) |
| **代码理解** | TreeSitter AST 索引 + 语义搜索 |
| **安全防护** | 双层 Guardrail（输入 SafetyCoordinator + 输出 SafeChatClient）|
| **沙箱执行** | Wasmtime v44（WASI capability 限制）|
| **成本追踪** | 全链路 token/cost 追踪，支持多用户预算隔离 |
| **可观测性** | OpenTelemetry 追踪 + 指标 |
| **会话持久化** | 自动保存到 `.livingtree/sessions/`，重启恢复 |
| **工具修复** | 自动 JSON 修复、循环检测、模糊匹配 |

## 项目结构

```
├── agents/                     # Agent 声明文件 (.agent.md)
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
│   └── LTAI.Host/              # ASP.NET Core 宿主
├── skills/                     # 可复用 Skill 脚本
├── docs/                       # 架构文档
└── tests/                      # 28 个单元测试
```

## Commands

```bash
dotnet build                    # 编译
dotnet test                     # 运行 28 个单元测试
dotnet run --project src/LTAI.TUI    # TUI 模式
dotnet run --project src/LTAI.Desktop # 桌面模式
docker-compose up               # Docker 部署
```

## Tech Stack

- **C# / .NET 10.0** — 17 个项目
- **Microsoft Agent Framework 1.8.0** — Agent pipeline
- **Wasmtime 44.0.0** — 沙箱执行
- **SQLite + FTS5** — 知识图谱存储
- **ONNX Runtime + BGE** — 本地嵌入
- **Spectre.Console** — TUI
- **Avalonia UI** — 桌面 UI
- **OpenTelemetry** — 可观测性
- **Serilog** — 结构化日志
