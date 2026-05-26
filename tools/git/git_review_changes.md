# tool: git_review_changes
domain: git
type: compose
description: Comprehensive review of all pending changes — diff + log + status overview

## steps
- status_overview
  command: git_status

- staged_diff
  command: git_diff
  input staged: true
  input stat: true

- unstaged_diff
  command: git_diff
  input stat: true

- recent_commits
  command: git_log
  input count: 5
  input format: oneline
  input graph: true

## triggers
- pattern: "查看所有改动" (weight: 1.0)
- pattern: "review changes" (weight: 0.9)
- pattern: "全面审查" (weight: 0.8)
- pattern: "有什么变化" (weight: 0.8)

## tags
- git
- compose
- safe
