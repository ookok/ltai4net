# tool: git_smart_pull
domain: git
type: compose
description: Safe pull workflow: stash local changes → fetch → check ahead/behind → pull → pop stash

## steps
- git_stash_before (parallel)
  command: git_stash
  input action: push
  input message: auto_stash_before_pull_{{timestamp}}

- git_fetch_latest (parallel)
  command: git_fetch
  input remote: {{remote}}
  input prune: true

- diff_ahead_behind
  command: git_diff
  input from: HEAD
  input to: origin/{{branch}}
  input stat: true

- pull_rebase
  command: git_pull
  input remote: {{remote}}
  input branch: {{branch}}
  input rebase: {{rebase}}

- pop_stash
  command: git_stash
  input action: pop

## parameters
- remote: string (default: "origin") — Remote name
- branch: string — Remote branch (default: current tracking branch)
- rebase: bool (default: true) — Use rebase instead of merge

## triggers
- pattern: "安全拉取" (weight: 1.0)
- pattern: "smart pull" (weight: 0.9)
- pattern: "拉取更新" (weight: 0.8)

## tags
- git
- compose
- safe
