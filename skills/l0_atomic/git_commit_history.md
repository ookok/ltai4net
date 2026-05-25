# skill: git_commit_history
domain: code/git
layer: 0
version: 1.0.0
intent: 查看 Git 提交历史记录
triggers:
  - pattern: "git\\s*log"
    weight: 1.0
  - pattern: "提交.*记录|最近.*提交|commit.*history"
    weight: 0.95
requires: []

## 步骤
1. shell: git log --oneline -10

## 验证
- must_contain: ""
