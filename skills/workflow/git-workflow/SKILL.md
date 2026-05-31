---
name: git-workflow
description: Git 工作流——提交信息规范/分支策略/PR 模板/常用操作流程
license: MIT
allowedTools: [ReadFileContent, Glob, RunCommand]
---

# Git Workflow

规范 Git 操作流程。

## 1. 提交信息格式（Conventional Commits）

```
<type>(<scope>): <subject>

<body>
```

| Type | 用途 |
|------|------|
| `feat` | 新功能 |
| `fix` | 修复 |
| `refactor` | 重构 |
| `test` | 测试 |
| `docs` | 文档 |
| `chore` | 构建/工具 |

示例：
```
feat(auth): add OAuth2 login flow

Implement GitHub OAuth2 authentication with state validation.
Closes #123
```

## 2. 分支策略（GitHub Flow）
```
main        ← 生产就绪
  └ feat/*  ← 新功能分支
  └ fix/*   ← 修复分支
```

- 从 `main` 创建功能分支
- PR 合并前必须 review
- 合并后删除功能分支

## 3. PR 描述模板
```markdown
## 变更内容
- 什么做了变更

## 测试
- [ ] 单元测试通过
- [ ] 手动测试

## 关联 Issue
Closes #xxx
```

## 4. 常用操作（GitTools）

| 场景 | 推荐工具 |
|------|---------|
| 查看当前变更 | `GitStatus()` / `GitDiff()` |
| 提交代码 | `GitAdd(".")` → `GitCommit("feat: ...")` → `GitPush()` |
| 查看历史 | `GitLog(count: 10)` / `GitBlame(file)` |
| 分支管理 | `GitBranch()` / `GitCheckout(target, createNew)` / `GitBranchDelete(name)` |
| 撤销操作 | `GitReset(target)` / `GitUndoLast()` |
| 同步远程 | `GitFetch()` → `GitRebase()` / `GitMerge(branch)` → `GitPush()` |
| 暂存工作 | `GitStash()` / `GitStashPop()` / `GitStashList()` |
| 标签发布 | `GitTag(name, message)` → `GitPush()` |
| Review 准备 | `GitReviewChanges()` / `GitDiff(@ref)` |
| 清理 | `GitCleanupBranches()` |
