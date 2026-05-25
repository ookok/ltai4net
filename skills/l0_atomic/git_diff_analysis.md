# skill: git_diff_analysis
domain: code/git
layer: 0
version: 1.0.0
intent: 解析 git diff 输出，识别变更类型
triggers:
  - pattern: "git diff"
    weight: 1.0
  - pattern: "diff "
    weight: 0.6
requires: []

## 步骤
1. shell: git diff --stat
2. shell: git diff

## 验证
- must_contain: "diff --git"
- pattern: "@@ -\d+,\d+ \+\d+,\d+ @@"
