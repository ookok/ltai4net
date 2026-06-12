# Batch 5: 代码智能升级 (CodeStruct + RepoShapley + EET)

**来源论文**:
- CodeStruct: Code Agents over Structured Action Spaces, ACL 2026
- RepoShapley: Shapley-Enhanced Context Filtering for Repository-Level Code Completion, ACL 2026
- EET: Experience-Driven Early Termination for Cost-Efficient SE Agents, ACL 2026
- CollabCoder: Plan-Code Co-Evolution, ACL 2026
- To Diff or Not to Diff: Structure-Aware Output Formats, ACL 2026

**优先级**: P2 | **工作量**: 12-18 人天 | **目标模块**: `src/LTAI.Agent.CodeAnalysis/`, `src/LTAI.Agent/Memory/CgGraph.cs`

## 现状分析与痛点

1. **文本级操作**: `FindInCode` / `GetSymbols` 返回原始文本片段。Agent 通过 `EditFile` 做文本替换，不知道自己在操作类/方法/属性等结构化实体。
2. **上下文膨胀**: 仓库级代码操作时，CgGraph 检索返回的上下文大量冗余，当前无智能过滤。
3. **重试浪费**: `GrammarCheckStep` 错误后 `ChatAgent` 自动重试 2 次，无失败模式识别，无法提前终止无效迭代。
4. **Diff vs Full File**: LLM 输出总是完整文件，在长文件中 token 浪费严重。
5. **计划-代码脱节**: `PlanTools` 生成的 Plan 和后续代码执行之间缺少反馈联动。

## 论文核心设计

### CodeStruct — AST 结构化动作空间

- 将代码仓库重新定义为基于 AST 的结构化动作空间
- Agent 通过命名的程序实体 (不是文本片段) 进行读取和编辑
- SWE-Bench Verified 提升 1.2-5.0% 准确率, token 消耗减少 12-38%

### RepoShapley — Shapley 上下文过滤

- 用 Shapley 值估计检索代码片段在组合中的交互贡献
- 保留高贡献片段, 丢弃冗余片段 (不是按单个相关性)
- 显著提升仓库级代码补全质量

### EET — 经验驱动早停

- 在补丁生成和补丁选择阶段识别无效迭代
- 历史经验 (同类错误模式) → 触发提前终止
- 总成本降低 19-55% (平均 32%), 几乎不损失性能

### CollabCoder — Plan-Code 共演化

- 协作决策模块判断错误应在计划层还是代码层修复
- 从错误中学习的自改进调试
- 比强基线提升 11-20%, 减少 4-10 次 API 调用

### To Diff or Not to Diff — 结构感知输出格式

- 自适应选择 full-code / block-diff / func-diff 格式
- 长代码编辑延迟和输出 token 降低 30%+

## 改进目标

1. CodeAnalysis 工具增加 AST 结构化操作
2. CgGraph 检索增加 Shapley 上下文过滤
3. GrammarCheckStep + ChatAgent 重试增加智能早停
4. 代码输出格式自适应 (diff vs full)
5. Plan 执行后自动反馈到 Plan 状态

## 详细设计

### 1. AST 结构化代码动作

```csharp
// 新增 src/LTAI.Agent.CodeAnalysis/StructuredCodeActions.cs

public class StructuredCodeActions
{
    // 替代 EditFile 文本替换 → 结构化操作
    Task<StructuredEditResult> EditSymbol(
        string filePath,
        string symbolName,        // 类名/方法名/属性名
        SymbolKind kind,          // Class, Method, Property, etc.
        string newImplementation, // 新实现代码
        EditMode mode             // Replace, Append, Prepend, Wrap
    );

    // 替代 ReadFileContent → 结构化读取
    Task<SymbolInfo> GetSymbolDetail(
        string filePath,
        string symbolName,
        bool includeBody = true,
        bool includeDocComment = true
    );

    // 替代 FindInCode → 结构化搜索
    Task<IReadOnlyList<SymbolReference>> FindSymbolReferences(
        string symbolName,
        string scopePath = null,  // 限定搜索范围
        ReferenceKind kind = ReferenceKind.Any // Definition, Call, Reference
    );
}

public class SymbolInfo
{
    public string Name { get; set; }
    public SymbolKind Kind { get; set; }
    public string ContainingType { get; set; }
    public string Signature { get; set; }
    public string Body { get; set; }
    public string DocComment { get; set; }
    public LocationSpan Location { get; set; }
    public IReadOnlyList<SymbolInfo> Members { get; set; } // for types
}
```

**实现策略**:
- C#: Roslyn `SemanticModel` → 精确符号解析
- Other: TreeSitter AST → 近似符号提取 (已支持 12+ 语言)

### 2. RepoShapley 上下文过滤

```csharp
// 修改 CgGraph.cs 检索方法

public async Task<IReadOnlyList<CodeSnippet>> RetrieveWithShapley(
    string query,
    int maxSnippets = 10,
    double shapleyThreshold = 0.1)
{
    // 1. 粗检索: 向量相似度 top-30
    var candidates = await VectorSearch(query, topK: 30);

    // 2. Shapley 值估计: 每个 snippet 对整体代码理解的边际贡献
    //    用 Monte Carlo 采样近似 (避免 O(2^n))
    var shapleyScores = EstimateShapleyValues(candidates, query, numSamples: 100);

    // 3. 过滤: 保留 Shapley 值 > threshold 的 snippet
    var filtered = candidates
        .Zip(shapleyScores, (s, score) => (snippet: s, score))
        .Where(x => x.score > shapleyThreshold)
        .OrderByDescending(x => x.score)
        .Take(maxSnippets)
        .Select(x => x.snippet)
        .ToList();

    return filtered;
}

// Shapley 值估算: 随机采样 snippet 子集, 计算每个 snippet 加入前后的相似度增量
// 使用 approximation: 样本数 = 100 已在原始论文验证精度足够
```

### 3. 智能早停机制

```csharp
// 修改 GrammarCheckStep.cs 和 ChatAgent.cs

public class SmartRetryController
{
    // 失败模式记录 (跨 task 持久化)
    ConcurrentDictionary<(string ErrorType, string FilePath), int> _errorModeCounts;

    // 重试决策
    RetryDecision Decide(GrammarCheckResult result, int attemptNumber)
    {
        // 模式 1: 连续 2 次同类型错误 → 早停, 切换策略
        var key = (result.ErrorType, result.FilePath);
        _errorModeCounts.AddOrUpdate(key, 1, (_, v) => v + 1);

        if (_errorModeCounts[key] >= 2)
            return RetryDecision.Stop("Repeated same error pattern, strategy change needed");

        // 模式 2: 错误数无减少趋势 → 早停
        if (attemptNumber >= 2 && result.ErrorCount >= _previousErrorCount)
            return RetryDecision.Stop("No improvement trend");

        // 模式 3: 修复引入了新错误 → 回滚 + 停
        if (result.NewErrorsIntroduced > result.ErrorsFixed)
            return RetryDecision.RevertAndStop();

        return RetryDecision.Continue();
    }
}
```

### 4. 自适应输出格式

```csharp
// 新增 AdaptiveCodeOutputFormatter.cs

public enum CodeOutputFormat { FullFile, BlockDiff, FuncDiff }

public class AdaptiveCodeOutputFormatter
{
    // 启发式规则
    public CodeOutputFormat SelectFormat(CodeEditContext context)
    {
        if (context.FileLineCount < 100)
            return CodeOutputFormat.FullFile;   // 小文件: 全量最简单

        if (context.EditsAreLocalized && context.EditRegionLineCount < 30)
            return CodeOutputFormat.FuncDiff;    // 局部修改 + 函数内: diff 省 token

        return CodeOutputFormat.BlockDiff;       // 长文件 + 跨函数: block diff
    }
}
```

**Token 节省估算**: 1000 行文件中修改 20 行, FuncDiff 约 200 tokens vs FullFile ~3000 tokens (93% 节省)

### 5. Plan-Code 反馈联动

```csharp
// 修改 PlanTools.cs 和 ExecutionEngine.cs

// Plan 执行后自动反馈:
// 1. 每步执行完成 → 标注 PlanStep.Status (Completed/Failed/Skipped)
// 2. 步骤失败 → 触发 CollabCoder 式决策: 是 Plan 有问题 (改 Plan) 还是 Code 有问题 (改 Code)?
// 3. Plan 修订 → 自动更新 PlanSteps, 无需人工干预
```

## 涉及文件清单

| 操作 | 文件 |
|------|------|
| 新增 | `src/LTAI.Agent.CodeAnalysis/StructuredCodeActions.cs` |
| 新增 | `src/LTAI.Agent.CodeAnalysis/SymbolResolver.cs` (Roslyn + TreeSitter 双引擎) |
| 修改 | `src/LTAI.Agent/Memory/CgGraph.cs` — Shapley 上下文过滤 |
| 新增 | `src/LTAI.Agent/Memory/ShapleyEstimator.cs` |
| 新增 | `src/LTAI.Agent/Pipeline/SmartRetryController.cs` |
| 修改 | `src/LTAI.Agent/Pipeline/Steps/GrammarCheckStep.cs` — 接入 SmartRetryController |
| 修改 | `src/LTAI.Agent/ChatAgent.cs` — 重试逻辑接入 SmartRetryController |
| 新增 | `src/LTAI.Agent/CodeGeneration/AdaptiveCodeOutputFormatter.cs` |
| 修改 | `src/LTAI.Agent/Tools/PlanTools.cs` — Plan-Code 反馈联动 |

## 验收标准

1. [ ] `EditSymbol("Foo.cs", "Bar", Method, newImpl)` 精确替换方法体 (C# + Python + JS)
2. [ ] Shapley 过滤后上下文 token 数减少 30%+, 代码补全质量无退化
3. [ ] 连续 2 次同类型 GrammarCheck 错误 → 自动停止重试 + 记录日志
4. [ ] 1000 行文件局部修改 → FuncDiff 格式输出, token 消耗 ≤ 全量输出的 30%
5. [ ] Plan Step 失败 → 自动触发 Plan-Code 决策, 3 次内收敛或降级
6. [ ] 现有 `EditFile` 文本操作接口保留兼容, 结构化接口作为新增选项

## 参考

- CodeStruct: ACL2026 Code Intelligence
- RepoShapley: ACL2026 Code Intelligence
- EET: ACL2026 Code Intelligence
- CollabCoder: ACL2026 Code Intelligence
- To Diff or Not to Diff: ACL2026 Code Intelligence
