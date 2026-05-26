# tool: git_commit_and_push
domain: git
type: compose
description: Stage all changes, commit, and push in one safe operation

## steps
- verify_clean
  command: git_status
  input short: true

- stage_all
  command: git_add
  input files: .

- commit
  command: git_commit
  input message: {{message}}

- push
  command: git_push
  input remote: {{remote}}
  input branch: {{branch}}

## parameters
- message: string (required) — Commit message
- remote: string (default: "origin") — Remote name
- branch: string — Branch to push (default: current branch)

## triggers
- pattern: "提交并推送" (weight: 1.0)
- pattern: "commit and push" (weight: 0.9)
- pattern: "快速提交" (weight: 0.8)

## tags
- git
- compose
