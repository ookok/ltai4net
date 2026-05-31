---
name: prompt-optimizer
description: 提示词优化——审查/重构 AI 提示词，提升输出质量和一致性
license: MIT
allowedTools: [ReadFileContent]
---

# Prompt Optimizer 提示词优化

审查和优化 AI 提示词（Prompt）。

## 1. 提示词结构检查

| 要素 | 说明 | 是否必需 |
|------|------|:--------:|
| 角色 | 你是谁 | ✅ |
| 任务 | 你要做什么 | ✅ |
| 上下文 | 背景信息 | 推荐 |
| 格式 | 输出格式要求 | ✅ |
| 示例 | Few-shot 示例 | 推荐 |
| 约束 | 限制条件 | ✅ |

## 2. 常见问题

- ❌ 指令模糊：`分析这段代码` → `分析这段代码的性能瓶颈和优化方案`
- ❌ 缺少格式：指定 JSON/Markdown/表格
- ❌ 没有否定指令：`不要输出解释`、`只输出代码`
- ❌ 角色缺失：`你是一个 C# 资深架构师`

## 3. 提示词模板

```
## Role
你是一个 {角色}

## Task
{具体任务描述}

## Context
{背景信息}

## Requirements
1. {约束1}
2. {约束2}

## Output Format
{期望的输出格式}

## Examples
输入: ...
输出: ...
```
