---
name: ppt-maker
description: PPT 生成——生成演示文稿大纲/内容/演讲备注/Marp 或 Reveal.js 格式
license: MIT
---

# PPT Maker 演示文稿生成

生成可直接用于演示工具的幻灯片内容。

## 1. 输出格式

默认输出 **Marp** 格式（Markdown 转 PPT），可直接在 VS Code 中预览：

```markdown
---
marp: true
theme: uncover
class: lead
---

# 标题

作者名

---

## 目录

1. 背景
2. 方法
3. 结果
4. 结论
```

也可选择 **Reveal.js** HTML 格式：

```html
<section>
  <h2>标题</h2>
  <p>内容</p>
</section>
```

## 2. 幻灯片结构

| 页数 | 内容 |
|:----:|------|
| 1 | 标题页：标题 + 副标题 + 作者 |
| 2 | 目录/议程 |
| 3-N | 正文（每页一个核心观点） |
| N+1 | 总结/结论 |
| N+2 | Q&A / 联系方式 |

## 3. 设计原则
- 每页不超过 7 行文字
- 核心数据用图表展示（`<table>` / `<chart>`）
- 关键数字加粗或放大字号
- 使用 `---` 分隔幻灯片

## 4. 演讲备注

用 `<!-- presenter notes -->` 添加备注：

```markdown
## 技术架构

<!-- 此处口头展开：讲清楚架构设计的三个关键决策 -->

- 前端: React + TypeScript
- 后端: .NET 10 + PostgreSQL
```
