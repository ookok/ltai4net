# tool: git_sync_fork
domain: git
type: compose
description: Sync a fork with upstream: fetch upstream → rebase/merge onto local main → push

## steps
- fetch_upstream
  command: git_fetch
  input remote: upstream
  input prune: true

- checkout_main
  command: git_branch_switch
  input name: {{main_branch}}

- merge_upstream
  command: git_merge
  input branch: upstream/{{main_branch}}

- push_main
  command: git_push
  input remote: origin
  input branch: {{main_branch}}

## parameters
- main_branch: string (default: "main") — Name of the main/default branch

## triggers
- pattern: "同步 fork" (weight: 1.0)
- pattern: "sync fork" (weight: 0.9)
- pattern: "更新 fork" (weight: 0.9)
- pattern: "上游同步" (weight: 0.8)

## tags
- git
- compose
