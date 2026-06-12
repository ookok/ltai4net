# Batch 4: 主动工具检索 (ToolOmni-inspired)

**来源论文**: [ToolOmni: Enabling Open-World Tool Use via Agentic Learning with Proactive Retrieval and Grounded Execution](https://arxiv.org/abs/2604.13787), ACL 2026
**优先级**: P2 | **工作量**: 5-8 人天 | **目标模块**: `src/LTAI.Agent/Tools/`, `src/LTAI.AI/`

## 现状分析与痛点

1. **被动检索**: `ToolFilteringChatClient` 的 BM25+ONNX→RRF→LLM 流程是被动的单轮检索。query→向量→topK→给 LLM。Agent 无法对检索结果做补充检索或改写查询。
2. **检索-执行耦合**: 工具检索质量和执行质量混在一起评估，无法区分解耦优化。
3. **大工具集退化**: 80+ tools, 单轮 top-8 容易遗漏关键工具，尤其复杂任务。
4. **无错误恢复**: 工具调用失败后无专门恢复链路 (当前靠 LLM 自发重试)。

## 论文核心设计

### 主动工具检索

```
Agent 在推理循环内自主决定:
  - 是否需要检索工具 (<search> 标签)
  - 搜索什么 (自主生成搜索查询)
  - 何时停止 (判断工具集是否足够)

多轮迭代:
  查询 → 嵌入模型 top-k → 判断是否足够 → 不够则改写查询再搜 → ... → 最终工具集 T_sub

优势: Agent 按任务复杂度动态调整搜索策略，复杂任务多搜几轮
```

### 解耦多目标训练 (LTAI 无需训练, 借鉴评估思路)

```
检索奖励 R_ret: 格式正确性 + 召回率 + 转化率
执行奖励 R_exec: 格式正确性 + 答案正确性
Separated Update: 检索和执行依次回传，避免信号干扰
```

### 关键数据

- ToolOmni 在 ToolBench 上端到端执行成功率超过强基线 +10.8%
- NDCG@1 和 @3 显著优于被动检索基线

## 改进目标

1. `ToolFilteringChatClient` 从单轮被动检索改为多轮主动检索
2. ToolRegistry 增加检索质量独立度量
3. 工具调用错误恢复机制 (借鉴 Fission-GRPO 思路)

## 详细设计

### 1. 主动工具检索循环

```csharp
// 修改 ToolFilteringChatClient.cs

public async Task<IReadOnlyList<AITool>> RetrieveToolsProactively(
    string userQuery,
    int maxRounds = 3,
    int toolsPerRound = 8,
    CancellationToken ct = default)
{
    var collected = new HashSet<AITool>();
    var searchQuery = userQuery;

    for (int round = 0; round < maxRounds; round++)
    {
        // 1. BM25 + ONNX 双路检索
        var candidates = await _toolRegistry.SearchAsync(searchQuery, toolsPerRound);

        // 2. RRF 融合 + LLM re-rank
        var selected = await RerankAndFilter(candidates, userQuery, collected);

        // 3. 合并到已收集集合
        foreach (var tool in selected)
            collected.Add(tool);

        // 4. LLM 判断工具集是否足够
        var sufficiency = await _llmClient.JudgeToolSufficiency(userQuery, collected.ToList());
        if (sufficiency.IsEnough)
            break;

        // 5. LLM 生成改写查询 (关注缺失的工具类型)
        searchQuery = sufficiency.SuggestedRefinementQuery;
    }

    return collected.ToList();
}
```

### 2. 检索质量独立度量

```csharp
// ToolRegistry.cs 新增
public class ToolRetrievalMetrics
{
    // 检索格式正确率: 是否正确使用了 tool schema
    public double FormatAccuracy { get; set; }

    // 召回率: ground-truth tools 中有多少被检索到
    public double Recall { get; set; }

    // 转化率: 检索到的工具中有多少被实际调用
    public double ConversionRate { get; set; }

    // 检索轮次: 平均需要几轮才能凑够工具集
    public double AvgRounds { get; set; }
}
```

### 3. 工具错误恢复 (借鉴 Fission-GRPO)

```csharp
// 新增 ToolErrorRecoveryHandler.cs

public enum ToolErrorType
{
    NotFound,         // 工具名/参数格式不正确 → 重选工具
    ExecutionFailed,  // 工具执行异常 → 重试或降级
    PermissionDenied, // 权限不足 → 告知用户
    Timeout           // 超时 → 重试或换等价工具
}

public class ToolErrorRecoveryHandler
{
    // 错误类型 → 恢复策略 映射
    Dictionary<ToolErrorType, RecoveryStrategy> _strategies;

    // 从失败中学习: 记录错误模式, 同类错误连续 2 次不再重试
    int _consecutiveSameError = 0;

    Task<ToolRecoveryResult> Recover(ToolCallFailure failure, AgentContext context);
}
```

### 4. 工具 Schema 适配 (借鉴 PA-Tool)

```csharp
// 从 Don't Adapt Small Language Models for Tools; Adapt Tool Schemas to the Models
// 利用"尖锐度"(peakedness) 信号识别模型预训练中熟悉的命名模式

// 对于 L1 小模型 (如 deepseek-chat), 自动重命名工具参数
// 使其对齐模型预训练中见过的命名惯例 (如将 "filePath" → "path")
public class ToolSchemaAdapter
{
    public ToolDefinition AdaptForModel(ToolDefinition original, string modelId);
}
```

## 涉及文件清单

| 操作 | 文件 |
|------|------|
| 修改 | `src/LTAI.Agent/Tools/ToolFilteringChatClient.cs` — 多轮主动检索 |
| 修改 | `src/LTAI.AI/ToolRegistry.cs` — 检索质量度量 |
| 新增 | `src/LTAI.Agent/Tools/ToolErrorRecoveryHandler.cs` |
| 新增 | `src/LTAI.Agent/Tools/ToolSchemaAdapter.cs` |
| 修改 | `src/LTAI.Agent/Tools/AgentToolStore.cs` — 接入检索度量 |

## 验收标准

1. [ ] 复杂任务 (需 5+ tool 协同) 工具召回率提升 10%+
2. [ ] 多轮检索平均 ≤ 2 轮收敛
3. [ ] 工具错误后恢复成功率 ≥ 60% (当前无专门机制)
4. [ ] 检索质量独立度量可观测 (OTel metrics)
5. [ ] 现有单轮路径 (query/tools 未变时跳过) 行为不变

## 参考

- ToolOmni: https://arxiv.org/abs/2604.13787
- ToolOmni code: https://github.com/Huangsz2021/ToolOmni
- Fission-GRPO: ACL2026 LLM Agent
- PA-Tool (Schema Adaptation): ACL2026 LLM Agent
