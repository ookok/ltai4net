---
description: 写作风格配方索引 — 供 LTAI-Writer 和其他写作 agent 使用
version: 1.0.0
---

# 风格配方索引 (Style Recipe Index)

受 [garden-skills style-recipes](https://github.com/ConardLi/garden-skills) 启发，每份配方是一个 Markdown 合约，
包含具体的语气、结构、词汇和格式约定。

## 配方列表

| 配方 | 最佳用途 | 语气 | 复杂度 |
|------|---------|------|--------|
| [technical-blog](technical-blog.recipe.md) | 技术博客、教程、技术分享 | 专业/清晰 | ★★★ |
| [release-note](release-note.recipe.md) | 版本发布说明、更新日志 | 简洁/精确 | ★☆ |
| [changelog](changelog.recipe.md) | 多版本变更日志 | 结构化/完整 | ★★ |
| [api-doc](api-doc.recipe.md) | API 参考文档、接口说明 | 规范/中立 | ★★★★ |
| [incident-report](incident-report.recipe.md) | 事故复盘、事后分析 | 坦诚/客观 | ★★★ |

## 如何添加配方

1. 在 `recipes/` 下创建 `<name>.recipe.md`
2. 遵循以下结构：
   - `---` front-matter (name, description, tone, audience)
   - `## Tone & Voice` — 语气和风格
   - `## Structure` — 文档结构
   - `## Vocabulary` — 词汇和表达
   - `## Anti-Patterns` — 应避免的模式
   - `## Examples` — 示例片段
3. 更新此 INDEX.md
