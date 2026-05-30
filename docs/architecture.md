# LTAI 架构文档

## 六层架构图

```mermaid
flowchart TB
    subgraph L5["L5 - Agent Layer"]
        direction LR
        CA["ChatAgent<br/>L1 flash → L2 pro"] --> WF["WorkflowOrchestrator<br/>Handoff / Sequential / Concurrent"]
        CA --> RL["Reflection Loop<br/>Quality self-check"]
        AR["AgentRegistry<br/>agents/*.agent.md"]
        WF --> S1["code"]
        WF --> S2["math"]
        WF --> S3["data"]
        WF --> S4["system"]
        WF --> S5["llm"]
        WF --> S6["writer"]
        WF --> S7["frontend"]
    end

    subgraph L4["L4 - Orchestration & Routing"]
        VR["Vector Router<br/>SelectTopK(task, k=5)"]
        PT["PlanTools<br/>Proposed → Approved → Executing → Completed"]
        WF --> VR
        VR -->|"Top-5 agents"| WF
    end

    subgraph L3["L3 - Tool System"]
        TCR["ToolCallRepairer<br/>JSON fix / Loop detect / Fuzzy match"]
        TR["ToolResult<br/>Success / Error JSON"]
        PM["Permission Matrix<br/>14 domains × 9 agents"]
        SUB["SubagentTools<br/>explore / research / review / security"]
    end

    subgraph L2["L2 - Memory & Knowledge"]
        KB["KbGraph<br/>KG + BM25 + CTE BFS"]
        CG["CgGraph<br/>Code AST Index<br/>TreeSitterParser"]
        KS["KgStore<br/>SQLite + FTS5 + WAL<br/>Vector(384d) + CTE"]
        RK["Reranker<br/>Cosine Sim → LLM Rescore"]
        KB --> KS
        CG --> KS
        KB --> RK
    end

    subgraph L1["L1 - LLM & Safety"]
        MPC["MultiProviderChatClient<br/>22 Providers<br/>Degradation Chain<br/>Circuit Breaker"]
        SCC["SafeChatClient<br/>Output Guard<br/>Non-streaming: block<br/>Streaming: buffer+check"]
        SC["SafetyCoordinator<br/>Input Guard<br/>Fail-closed"]
        EC["EmbeddingClient<br/>API → ONNX(BGE) → BM25<br/>FastEmb fallback"]
        CS["CompactStrategy<br/>Verified Summarization<br/>→ truncation fallback"]
        MPC --> SCC
    end

    subgraph L0["L0 - Runtime & Infrastructure"]
        MAF["MAF Pipeline<br/>ChatClientAgent<br/>AIContextProvider Chain"]
        BT["BudgetTracker<br/>Global: 1M / User: 200K"]
        UT["UsageTracker<br/>IUsageTracker + DI Scoped"]
        WS["WasmtimeSandbox<br/>WASM execution v44<br/>WASI restrictions"]
        OT["OpenTelemetry<br/>Traces + Metrics"]
        SP["Session Persistence<br/>.livingtree/sessions/"]
        MAF --> KB
        MAF --> CG
        MAF --> SC
        MAF --> CS
        MAF --> WS
    end

    subgraph CC["Cross-Cutting"]
        TID["TraceId Propagation<br/>ChatAgent → Workflow → Subagent"]
    end

    L5 --> L4 --> L3 --> L2 --> L1 --> L0
    CC -.-> L5
    CC -.-> L4
    CC -.-> L0
```

## 层间合约

| 层 | 职责 | 消费 | 被消费 |
|----|------|------|--------|
| L5 Agent | 用户交互、任务路由 | L4 Orchestration | UI (TUI/Desktop/CLI) |
| L4 Orchestration | Agent 编排、向量路由 | L3 Tools + L5 Agents | L5 Agent |
| L3 Tool System | 工具执行、权限控制 | L2 Memory | L4 Orchestration |
| L2 Memory | 知识/代码图谱、语义检索 | L1 LLM | L3 Tools |
| L1 LLM & Safety | LLM 路由、安全防护 | L0 Runtime | L2 Memory |
| L0 Runtime | MAF 管线、沙箱、监控 | - | 全层 |

## 数据流

```
User Input → ChatAgent → Budget Check → Safety Input Guard → KbGraph/CgGraph Context Injection
  → Compaction → WasmtimeSandbox → MultiProviderChatClient (LLM)
  → ToolCallRepairer (if tool call) → Tool Execution → ToolResult
  → SafeChatClient Output Guard → Reflection Loop → Response
```

## 关键设计决策

| 决策 | 选择 | 理由 |
|------|------|------|
| Agent 路由 | LLM + 向量预选 | 20+ Agent 时 prompt 不膨胀 |
| 知识图谱 | SQLite FTS5 + CTE | 零运维、单用户够用 |
| 沙箱 | Wasmtime v44 | 成熟度 (129万下载) > Hyperlight (v0.4) |
| 上下文压缩 | LLM 摘要 + 验证 | 防幻觉累积 |
| UsageTracker | 静态 API + DI 接口 | 不改旧代码，增量支持多租户 |
| 会话持久 | .livingtree/sessions/ | 进程重启不丢上下文 |
