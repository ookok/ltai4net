---
name: release-note
description: 版本发布说明 — 简洁精确，聚焦变更
tone: concise, precise, neutral
audience: users, developers, operations
version: 1.0.0
---

## Tone & Voice

- **极简主义**：只说变更了什么，不说为什么（除非非显而易见）
- **用户视角**：按对用户的影响组织，而非按代码提交
- **中性语气**：不营销，不夸张

## Structure

```markdown
## [版本号] - 发布日期

### ✨ 新功能
- 功能名称：一句话说明（附 issue/PR 编号）

### 🔧 改进
- 改进项：一句话说明

### 🐛 修复
- 修复项：一句话说明

### ⚠️ 不兼容变更
- 变更项：旧行为 → 新行为
```

## Vocabulary

- 使用过去时："添加了"、"修复了"、"移除了"
- 避免："我们很高兴地宣布"、"终于"、"重磅"
- 使用精确版本号：`v1.2.3` 而非 `最新版本`

## Example

```markdown
## v2.1.0 - 2026-06-22

### ✨ 新功能
- AntiPatternCheckStep：自动检测 AI 俗套、硬编码密钥、代码反模式
- 多维评分 QualityGate：使用 5 维度 critique 替代单一分数

### 🔧 改进
- PipelineRunner 支持 AntiPatternBlocked 阻断标志
- Agent YAML front-matter 增加 version/manifest/recipes 字段
```
