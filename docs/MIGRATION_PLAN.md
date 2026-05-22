# LTAI v6.0 — 迁移实施计划

## Phase 0: 清理 & 方案保存 ✅

- [x] 删除旧 docs/（Architecture.md, TOOLS.md, etc.）
- [x] 保存 ARCHITECTURE_v6.md（新架构方案）
- [x] 保存 MIGRATION_PLAN.md（本文件）

---

## Phase 1: 项目结构重组 (Week 1-2)

### 新建项目（创建 csproj + 目录 + 基础代码）
- [ ] **LTAI.Models** — 共享模型层（从 LTAI.Core 和 LTAI.Execution.Models 提取）
- [ ] **LTAI.Agent** — MAF 原生 Agent 层（替换 LTAI.MAF）
  - Agents/ChatAgent.cs, CodeAgent.cs, EIAAgent.cs, ReasoningAgent.cs
  - Middleware/PromptShieldMiddleware.cs, InputClassifierMiddleware.cs, DNASafetyMiddleware.cs, OutputReviewMiddleware.cs
  - Workflows/AgentMeshWorkflow.cs
  - Skills/ 目录
  - agents.yaml（声明式 Agent 配置）
- [ ] **LTAI.Knowledge** — 合并 Vector + Document + Memory
  - Embedding/, Storage/, RAG/, Parsers/, Quality/
- [ ] **LTAI.Tools** — 合并 Capability + MAF Tools
  - General/, Code/, EIA/, GIS/
- [ ] **LTAI.Infra** — 合并 Sandbox + Browser + Network + Multimodal
- [ ] **LTAI.Planning** — 合并 Execution + Metrics
  - Planner/, Quality/, Observability/

### 溶解项目（移除 csproj，代码迁移到新项目）
- [ ] LTAI.MAF → LTAI.Agent
- [ ] LTAI.Vector → LTAI.Knowledge
- [ ] LTAI.Document → LTAI.Knowledge
- [ ] LTAI.Capability → LTAI.Tools
- [ ] LTAI.Sandbox → LTAI.Infra
- [ ] LTAI.Browser → LTAI.Infra
- [ ] LTAI.Network → LTAI.Infra
- [ ] LTAI.Multimodal → LTAI.Infra
- [ ] LTAI.Execution → LTAI.Planning
- [ ] LTAI.Metrics → LTAI.Planning
- [ ] LTAI.Execution.Models → LTAI.Models
- [ ] LTAI.TreeLLM → 溶解到 LTAI.Agent + LTAI.AI
- [ ] LTAI.Memory → LTAI.Knowledge

### 保留项目（精简）
- [ ] LTAI.Core（移除 Execution、Life、Multimodal、Network 子目录，提取 Models）
- [ ] LTAI.AI（移除 Governors/ — 溶解到 Provider + L1/L2 + CellAI）
- [ ] LTAI.DNA（30+ → 8 子系统）
- [ ] LTAI.Economy（精简）
- [ ] LTAI.Web（保持）
- [ ] LTAI.Host（重写 Program.cs）
- [ ] LTAI.TUI / LTAI.MCP / LTAI.Desktop / LTAI.WebApp（统一 Host Builder）

### 更新全局配置
- [ ] 更新 LTAI.sln（目标：10 个项目 + tests）
- [ ] 更新 Directory.Build.props
- [ ] 更新 docker-compose.yml
- [ ] 更新 Dockerfile

---

## Phase 2: Agent 层重构 (Week 3-4)

### 2.1 Agent 实现
- [ ] `ChatAgent` — 通用对话，继承 `AIAgent`，使用 `ChatClientAgent`
- [ ] `CodeAgent` — 代码专家，注入代码工具集
- [ ] `EIAAgent` — 环评专家，注入 EIA/GIS 工具集
- [ ] `ReasoningAgent` — 深度推理，集成 MCTS + 多模型共识

### 2.2 Workflow 迁移
- [ ] `AgentMeshWorkflow` — MAF Graph Workflow 替代 GovernorWorkflow
- [ ] IntentAnalyzer → ContextInjector → AgentSelect → OutputFormatter
- [ ] 条件路由 (code/chat/eia/reflex)

### 2.3 Middleware 迁移
- [ ] `PromptShieldMiddleware` — Prompt 注入防护
- [ ] `InputClassifierMiddleware` — 意图分类
- [ ] `DNASafetyMiddleware` — DNA 安全检查
- [ ] `OutputReviewMiddleware` — 输出审核

### 2.4 工具统一
- [ ] 全部工具迁移到 MAF `AIFunction` 标准
- [ ] 工具分类注册 (General/Code/EIA/GIS)
- [ ] 移除 AIToolRegistry，使用 Agent.Tools.Add()

### 2.5 LivingTreeSystem 精简
- [ ] 移除 Governor 依赖链 (10+ 构造函数参数)
- [ ] 改为轻量协调器 (<200行)
- [ ] 仅保留: Provider 选择 + L1L2 路由 + 安全校验

---

## Phase 3: DNA & Host 重构 (Week 5-6)

### 3.1 DNA 简化 (30+ → 8)
- [ ] PersonaEngine — Big Five + Identity → Agent Instructions
- [ ] SafetyGuard — Content safety → AgentMiddleware
- [ ] MemorySystem — Emotional memory → AgentSession + Persistence
- [ ] LifeEngine — Biorhythm → BackgroundService
- [ ] EvolutionEngine — Self-improvement → AgentHarness
- [ ] ToolRepair — Self-healing → AIFunction error handler
- [ ] FeedbackLoop — RLVR → Evaluation
- [ ] IdentityNarrative — Identity → Declarative config

### 3.2 Host 统一
- [ ] `LTAIHostBuilder` — 统一 Host 构建器
- [ ] Host Profile 切换 (webapi/tui/mcp/desktop/webapp)
- [ ] 声明式 Agent 配置加载 (agents.yaml)
- [ ] First-run 配置向导集成

---

## Phase 4: 验证 & 打磨 (Week 7-8)

- [ ] 端到端测试 (126+ 工具验证)
- [ ] 性能 Benchmark vs v5.5
- [ ] Docker Compose 端到端集成
- [ ] OpenTelemetry 链路追踪验证
- [ ] MAF DevUI 集成验证
- [ ] README.md 更新
- [ ] API 文档更新

---

## 破坏性变更摘要

1. **LTAI.MAF 命名空间 → LTAI.Agent**
2. **AIToolRegistry → Agent.Tools (AIFunction)**
3. **LivingTreeSystem → 精简协调器**
4. **Governor 概念 → MAF Workflow/AgentMiddleware**
5. **LivingTreeChatClient → ChatClientAgent (MAF 原生)**
6. **GovernorWorkflow → AgentWorkflow (MAF 原生)**
7. **DNAOrchestrator (30+) → 8 独立模块**
8. **5 套启动逻辑 → 1 个 LTAIHostBuilder**

---

*最后更新: 2026-05-22*
