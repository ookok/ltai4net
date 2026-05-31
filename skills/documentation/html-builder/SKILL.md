---
name: html-builder
description: HTML 页面生成——响应式布局/语义化标签/纯 CSS 或 Tailwind/Bootstrap
license: MIT
allowedTools: [ReadFileContent, WriteFileContent, Glob]
---

# HTML Builder 网页生成

生成可直接运行的 HTML 页面。

## 1. 页面骨架

```html
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>页面标题</title>
  <style>
    /* CSS */
  </style>
</head>
<body>
  <script>
    // JavaScript
  </script>
</body>
</html>
```

## 2. UI 框架选择

| 框架 | 引入方式 | 适用场景 |
|------|---------|---------|
| 纯 CSS | 内联 `<style>` | 轻量页面 |
| Tailwind CSS | CDN: `<script src="https://cdn.tailwindcss.com">` | 快速原型 |
| Bootstrap 5 | CDN: `<link href="..." rel="stylesheet">` | 企业应用 |
| 无框架 | 自定义 CSS 变量 | 设计稿还原 |

## 3. 响应式设计
- 移动优先：`flex-wrap` / `grid` / `@media`
- 断点：sm:640px / md:768px / lg:1024px / xl:1280px
- 字体：`clamp(14px, 2vw, 18px)` 自动缩放

## 4. 图标
- 内联 SVG（推荐）：直接嵌入、可着色
- 图标库：`<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.0.0/css/all.min.css">`

## 5. 输出要求
- 单个 HTML 文件，所有资源内联
- 可直接在浏览器中打开运行
- 中文优先，字体栈: `system-ui, "PingFang SC", sans-serif`
