# Batch 3: Workflow Graph IR + 自然语言编译 (MASFactory-inspired)

**来源论文**: [MASFactory: A Graph-centric Framework for Orchestrating LLM-Based Multi-Agent Systems with Vibe Graphing](https://arxiv.org/abs/2603.06007), ACL 2026
**优先级**: P1 | **工作量**: 8-12 人天 | **目标模块**: `src/LTAI.Agent/Workflows/`, `src/LTAI.Agent/Orchestration/`

## 现状分析与痛点

1. **固定模式**: AgentWorkflows 仅 Handoff / Sequential / Concurrent 三种，扩展新 pattern 需要写 C# 代码
2. **手工 YAML**: `.livingtree/workflows/*.yaml` 需手工编写，无自然语言驱动
3. **无可视化**: Workflow 图结构不可视化，debug 困难
4. **复用差**: 相似 pattern (review-revise, plan-code-review) 每次要重新配置
5. **不可离线调试**: 无 workflow 级别的 offline evaluation

## 论文核心设计

### Vibe Graphing 三阶段编译

```
自然语言意图 → Role Assignment → Structure Design → Semantic Completion → 可执行 Workflow

Stage 1 (Role Assignment):  意图 → 带边界的候选 Agent 角色列表
Stage 2 (Structure Design): 角色信息依赖 → 有向图拓扑骨架 (连通性 + 消息/控制流方向)
Stage 3 (Semantic Completion): 骨架 → 参数化实例 (prompt + tools + 通信协议)
```

### 关键抽象

1. **IR 层**: 每个阶段产出可读可编辑的结构化中间表示，LLM 只负责填语义，框架保证可执行性
2. **Context/Message Adapter**: 把 Mem0/RAG/MCP 等异构 context source 统一为标准接口，图拓扑与外部生态解耦
3. **ComposedGraph 模板**: 预定义协作子图 (review-critique-revise, propose-vote-merge 等), 可 clone + 参数化
4. **VS Code Visualizer**: 拓扑预览 + runtime tracing + human-in-the-loop 编辑

### 关键数据

- ChatDev 1511 行 → 45 行 Vibe Graphing 描述
- Vibe Graphing 构造成本 $0.26 vs Vibe Coding $3+ (10× 成本优势)
- 复现版 MetaGPT 超原版 (HumanEval +22pp) — 脏工程实现被模板解耦后方法论效果显现

## 改进目标

1. 建立 `WorkflowGraphIR` 中间表示层
2. 实现 `WorkflowCompiler` 自然语言 → Graph IR 编译
3. 建立 `ComposedWorkflow` 模板库
4. DevUI 中添加 workflow 可视化
5. (远期) 支持离线评估与迭代精化 (PROTEA-inspired)

## 详细设计

### 1. WorkflowGraphIR 中间表示

```csharp
// 新增 src/LTAI.Agent/Workflows/GraphIR/

public class WorkflowGraphIR
{
    public string Name { get; set; }
    public List<GraphNode> Nodes { get; set; }
    public List<GraphEdge> Edges { get; set; }
    public GraphMetadata Metadata { get; set; }
}

public class GraphNode
{
    public string Id { get; set; }
    public NodeType Type { get; set; }    // Agent, Loop, Switch, Interaction, SubGraph
    public string AgentName { get; set; } // 绑定的 Agent
    public string PromptTemplate { get; set; }
    public List<string> Tools { get; set; }
    public NodeConfig Config { get; set; }
}

public class GraphEdge
{
    public string From { get; set; }
    public string To { get; set; }
    public EdgeType Type { get; set; }    // Control, Message, State
    public string Condition { get; set; } // 条件表达式 (for Switch edges)
}

public enum NodeType { Agent, Loop, Switch, Interaction, SubGraph }
public enum EdgeType { Control, Message, State }
```

**与 YAML 的关系**:
- GraphIR 是 YAML workflow 的超集，表达能力更强
- 现有 YAML workflow 可自动转换为 GraphIR
- GraphIR 可序列化为 JSON/YAML 用于持久化

### 2. WorkflowCompiler (NL → IR)

```csharp
// 新增 src/LTAI.Agent/Workflows/GraphIR/WorkflowCompiler.cs

public class WorkflowCompiler
{
    // Stage 1: LLM 解析 NL → 角色列表
    Task<List<AgentRole>> AssignRoles(string intent);

    // Stage 2: LLM 根据角色依赖 → 图拓扑
    Task<WorkflowGraphIR> DesignStructure(List<AgentRole> roles, string intent);

    // Stage 3: LLM 填充 prompt + tools
    Task<WorkflowGraphIR> CompleteSemantics(WorkflowGraphIR skeleton);

    // 端到端编译
    Task<WorkflowGraphIR> Compile(string naturalLanguageIntent);

    // Human-in-the-loop: 每阶段产出 IR 后可选暂停等待人审
    bool PauseAfterEachStage { get; set; }
}
```

### 3. ComposedWorkflow 模板库

```yaml
# .livingtree/workflows/templates/review-revise.yaml
name: review-revise
parameters:
  - name: reviewer_agent
    type: agent_name
    default: LTAI-Review
  - name: max_iterations
    type: integer
    default: 3
graph:
  nodes:
    - id: generate
      agent: "{{.params.author_agent}}"
    - id: review
      agent: "{{.params.reviewer_agent}}"
    - id: switch
      type: Switch
      condition: "review.approved || iterations >= {{.params.max_iterations}}"
  edges:
    - from: generate
      to: review
      type: Control
    - from: review
      to: switch
      type: Control
    - from: switch
      to: generate
      type: Control
      condition: "!review.approved"
    - from: switch
      to: end
      type: Control
      condition: "review.approved"
```

预置模板:
- `review-revise`: 生成 → 评审 → 修改 循环
- `plan-code-review`: 规划 → 编码 → 评审 链
- `debate-consensus`: K agent 辩论 → 仲裁
- `explore-exploit`: 搜索 → 分析 → 决策

### 4. DevUI 可视化 (Phase 2)

- 新增 `/devui/workflows` 页面
- GraphIR → Mermaid/D3.js 渲染拓扑图
- 节点点击查看 prompt + tools 配置
- Runtime tracing: 实时显示当前执行节点
- 热编辑: 拖拽修改拓扑 → 序列化回 GraphIR

### 5. 离线评估 (PROTEA-inspired, Phase 3)

```
PROTEA 核心: 节点级评估 → 反向生成中间期望 → 可编辑 prompt 修订
             把"最终答案变差了"定位到具体节点并闭环验证
```

远期可为 LTAI workflow 增加:
- Workflow 运行录屏 (输入 → 每节点输出)
- 节点级质量打分 (自动 + 人工)
- 失败回溯: "这个 bug 在哪个 agent 引入的?"

## TUI/CLI 命令扩展

```bash
ltai workflow compile "先让架构师分析，再让安全审查，最后让写手生成报告"
  # → 输出 GraphIR JSON, 可选直接注册为 workflow

ltai workflow visualize <name>
  # → 打印 ASCII/Mermaid 拓扑图到终端

ltai workflow template list
  # → 列出预置模板

ltai workflow template apply review-revise --author=LTAI-Code --reviewer=LTAI-Review
  # → 用模板参数化生成新 workflow
```

## 涉及文件清单

| 操作 | 文件 |
|------|------|
| 新增 | `src/LTAI.Agent/Workflows/GraphIR/WorkflowGraphIR.cs` |
| 新增 | `src/LTAI.Agent/Workflows/GraphIR/WorkflowCompiler.cs` |
| 新增 | `src/LTAI.Agent/Workflows/GraphIR/GraphIRSerializer.cs` |
| 新增 | `src/LTAI.Agent/Workflows/GraphIR/ComposedWorkflow.cs` |
| 新增 | `.livingtree/workflows/templates/*.yaml` |
| 修改 | `src/LTAI.Agent/Workflows/YAMLWorkflowRegistry.cs` — 增加 GraphIR 加载路径 |
| 修改 | `src/LTAI.Cli/Commands/WorkflowCommands.cs` — 新增 compile/visualize/template 命令 |
| 修改 | `src/LTAI.Web/DevUI/` — workflow 可视化页面 (Phase 2) |

## 验收标准

1. [ ] "先让 X 做 A，再让 Y 做 B，不通过就循环" → 编译为正确 GraphIR
2. [ ] GraphIR 可序列化为 JSON 并反序列化回等价结构
3. [ ] 3 个预置模板通过参数化生成可执行 workflow
4. [ ] 现有 YAML workflow 可无损转换为 GraphIR (往返测试)
5. [ ] TUI `workflow compile` 命令可用

## 参考

- MASFactory: https://arxiv.org/abs/2603.06007
- MASFactory code: https://github.com/BUPT-GAMMA/MASFactory
- PROTEA: ACL2026 Multi-Agent
