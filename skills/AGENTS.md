# Skill 编写指南

每个 Skill 是一个独立的子目录，包含 `SKILL.md` 主文件，可选 `*.csx` 脚本和资源文件。

## 目录结构

```
skills/<domain>/<skill-name>/
├── SKILL.md        # 技能定义（必需）
├── run.csx         # C# 脚本（可选，Roslyn Scripting 执行）
└── *.csx           # 其他辅助脚本（可选）
```

## SKILL.md 格式

```yaml
---
name: <技能名称>                          # 必需，LLM 通过此名称 load_skill
description: <一句话描述>                 # 必需，LLM 据此判断是否匹配任务
license: MIT                             # 可选
allowedTools: [Tool1, Tool2]             # 可选，技能依赖的工具列表
version: 1.0.0                          # 可选，语义版本
deprecated: false                        # 可选，true 表示已废弃
supersededBy: <new-skill-name>           # 可选，被哪个技能替代
validated: 2026-06-01                    # 可选，最后验证日期，超过 90 天告警
---

# 标题

## 概要
<!-- L2 层：llm load_skill 后先读此节，3-5 条核心步骤 -->
<!-- 目的：让 LLM 快速掌握技能要点，不必通读全文 -->
- 步骤 1：...
- 步骤 2：...
- 关键参数：...
- 输出：...

## 详细内容
<!-- L3 层：llm 需要深入了解时阅读 -->
... markdown 指令正文 ...
```

## allowedTools 指引

`allowedTools` 告诉 Tool RAG 这个技能需要哪些工具。Tool RAG 只保留 top-8 最相关的工具，如果技能需要的工具被过滤掉，LLM 加载了 skill 也无法执行。

### 常用工具参考

| 工具 | 用途 | 适用技能 |
|------|------|----------|
| `ReadFileContent` | 读取文件内容 | 所有分析类技能 |
| `SearchContent` | 全文搜索（grep） | 代码审查、迁移 |
| `FindInCode` | 精确查找标识符定义/引用 | 重构、审计 |
| `Glob` | 按模式查找文件 | 文件遍历 |
| `DirectoryTree` | 目录树 | 架构分析 |
| `ListFiles` | 列出目录文件 | 通用 |
| `WriteFileContent` | 写文件 | 文档生成 |
| `BuildDocument` | 生成 Office 文档 | 文档生成 |
| `WebSearch` | 网络搜索 | 竞品分析 |
| `RunCommand` | 执行 shell 命令 | Git 工作流、性能分析 |
| `CSharpScript` | 执行 C# 脚本 | 数据分析 |

### 规则
- 不写 `allowedTools` = 所有工具可用（Tool RAG 行为不变）
- 写了 `allowedTools` = Tool RAG 会优先保留这些工具
- 不要列出 `load_skill`（自动可用）

## 脚本（可选）

如果技能需要自动化执行，可以在同目录下放 `run.csx`（C# Script）：

```csharp
#r "nuget: Some.Package, 1.0.0"
using System.Text.Json;

var input = args.Length > 0 ? args[0] : "";
var data = JsonSerializer.Deserialize<JsonElement>(input);
// ... 你的逻辑 ...
return JsonSerializer.Serialize(result);
```

脚本通过 `SkillScriptRunner.RunAsync` 由 `dotnet script` 或 `dotnet run` 执行，60 秒超时。

## 最佳实践

1. **description 要精准** — LLM 根据 description 决定是否加载此 skill，描述要清晰且独特
2. **allowedTools 要完整** — 列出技能执行所需的所有核心工具
3. **正文结构化** — 使用 `## 标题` + 列表 + 表格，LLM 容易遵循
4. **输出格式明确** — 在 `## 输出格式` 中给出 JSON 或 Markdown 模板
5. **示例优先** — 提供输入/输出示例，LLM 表现更好

## 2026 H1 论文启发

| # | 论文 | 实现位置 | 状态 |
|---|------|---------|------|
| 1 | DLLG — token 级 logit 门控 | `FusionRoute/ResponseSpanRouter.cs` Stitch 自适应融合比 | 已实现 |
| 2 | UCCI — 校准不确定性 | `Agents/ChatAgent.cs` CalibrateUncertainty | 已实现 |
| 3 | MemGraphRAG — 多 agent KG 轨迹 | `Vector/KgExplorationTrace.cs` | 已实现 |
| 4 | Beyond Consensus — trace 级合成 | `Workflows/AgentWorkflows.cs` concurrent aggregator | 已实现 |
| 5 | CODESKILL — 技能银行 | `Tools/SkillBank.cs` | 已实现 |
| 6 | Bayesian Orchestration — VoI 路由 | `Agents/ChatAgent.cs` EstimateValueOfInformation | 已实现 |
| 7 | EDRM — 熵相变路由 | `Agents/ChatAgent.cs` EstimateResponseEntropy | 已实现 |
| 8 | SEE — 自评估 | `Agents/ChatAgent.cs` JudgeResponseQualityAsync | 已实现 |
| 9 | AdaDPO — DPO 梯度修复 | 未训练管线，标记待接入 | 待实现 |
| 10 | PACT — 协议化消息 | `Tools/SubagentTools.cs` GetSystemPrompt | 已实现 |

### AdaDPO（#9）接入说明

当前项目无 DPO 训练管线。接入 AdaDPO 需：
1. 在 `LTAI.Agent.Training` 命名空间创建 DPO 数据集加载器
2. 用 `SkillBank.RecordUse` 轨迹作为偏好对（成功/失败）
3. `AdaDPO` 的 per-pair stop-gradient 系数只需在 loss 函数中加一行：
   ```python
   loss = -torch.mean(log_ratio * (1 - stop_grad(coeff)) + log_ratio * stop_grad(coeff))
   ```

## 过期 / 陈旧 Skill 处理

Skill 会随版本演进而过期。系统通过以下机制管理生命周期：

### 检测信号

| 信号 | 来源 | 动作 |
|------|------|------|
| `validated` 超 90 天 | SKILL.md front-matter | `ltai skill audit` 警告 |
| `deprecated: true` | SKILL.md front-matter | `load_skill` 提示废弃 |
| useCount==0 && age>180d | SkillEvolutionEngine | 自动标记 deprecated |
| successRate<0.3 && call>5 | SkillEvolutionEngine | L3 prune 自动删除 |

### CLI

```bash
ltai skill list              # skill + 状态
ltai skill audit             # 检查过期
ltai skill deprecate <name>  # 标记废弃
ltai skill prune             # 删除过期
```

### 手动处理

1. 标记废弃: SKILL.md 加 `deprecated: true` + `supersededBy: <new>`
2. 更新验证: 审核后更新 `validated: $(date +%F)`
3. 引用迁移: `grep -r "load_skill.*<name>"` 找出引用点`
