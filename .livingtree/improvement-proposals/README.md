# LTAI 改进方案 — ACL2026 论文驱动

> 基于 [ACL2026 1363 篇论文](https://papernotes.org/ACL2026/) 中 4 个核心方向
> (LLM Agent 78 + 多智能体 38 + 代码智能 51 + 信息检索/RAG 73) 的检索分析，
> 结合 LTAI 项目架构审计，生成 5 批次改进方案。

## 论文-方案映射总览

| 批次 | 核心论文 | 目标模块 | 优先级 | 工作量 |
|------|---------|---------|--------|--------|
| [Batch 1](batch-01-magma-memory.md) | MAGMA | 记忆系统 (KbGraph/MemoryPalace) | P0 | 15-20d |
| [Batch 2](batch-02-orchestration-diversity.md) | Diversity Collapse + MoA Pareto + MASFactory | 编排系统 (AgentWorkflows) | P1 | 10-15d |
| [Batch 3](batch-03-masfactory-graph-ir.md) | MASFactory + PROTEA | Workflow IR + 离线调试 | P1 | 8-12d |
| [Batch 4](batch-04-toolomni-retrieval.md) | ToolOmni + MCP-Flow | 工具系统 (ToolRegistry) | P2 | 5-8d |
| [Batch 5](batch-05-code-intelligence.md) | CodeStruct + RepoShapley + EET | 代码分析 (CodeAnalysis) | P2 | 12-18d |

## 论文深度阅读记录

| 论文 | 链接 | 核心贡献 |
|------|------|---------|
| MAGMA | [arxiv:2601.03236](https://arxiv.org/abs/2601.03236) | 四图记忆 + intent路由 + 双流写入 |
| Diversity Collapse | [arxiv:2604.18005](https://arxiv.org/abs/2604.18005) | 结构耦合驱动多样性崩溃，NGT/子组缓解 |
| MASFactory | [arxiv:2603.06007](https://arxiv.org/abs/2603.06007) | Vibe Graphing 三阶段编译 NL→workflow |
| Multi-Agent Reasoning | [arxiv:2605.01566](https://arxiv.org/abs/2605.01566) | MoA Pareto最优，proposer=layer+1 |
| ToolOmni | [arxiv:2604.13787](https://arxiv.org/abs/2604.13787) | 主动工具检索 + 解耦GRPO |
| CodeStruct | ACL2026 | AST结构化的代码动作空间 |
| RepoShapley | ACL2026 | Shapley增强的仓库上下文过滤 |
| EET | ACL2026 | 经验驱动SE agent早停 |

## 实施策略

1. **Batch 1-2 先做**: 记忆 + 编排是核心链路，收益最大
2. **Batch 3 紧随**: Graph IR 为后续所有编排改进提供基础
3. **Batch 4-5 后置**: 工具和代码优化在核心链路稳定后再做
4. **每批次独立可验证**: 每批有明确验收标准，不互相阻塞

## 生成时间

2026-06-12
