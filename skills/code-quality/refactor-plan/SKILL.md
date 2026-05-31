---
name: refactor-plan
description: 重构计划——分析依赖、风险评估、分步执行方案
license: MIT
allowedTools: [ReadFileContent, SearchContent, FindInCode, Glob, DirectoryTree]
---

# Refactor Plan 重构计划

对指定代码进行安全重构，按以下流程执行：

## 阶段 1: 分析
1. 识别重复代码、过长方法、职责混杂的类
2. 绘制调用依赖图（谁依赖谁）
3. 评估重构风险（API 变更、测试影响）

## 阶段 2: 计划
1. 分解为可独立验证的小步骤
2. 每步包含：目标文件、改动内容、验证方法
3. 标注高风险步骤（需要特别关注）

## 阶段 3: 执行
1. 每步完成后立即运行测试
2. 使用 `mark_step_complete` 跟踪进度
3. 遇到意外复杂度时调用 `revise_plan` 调整

## 安全准则
- 不修改公共 API 签名（除非明确允许）
- 保持向后兼容
- 每步可回滚
