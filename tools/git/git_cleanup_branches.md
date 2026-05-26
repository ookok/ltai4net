# tool: git_cleanup_branches
domain: git
type: compose
description: Find merged branches and offer cleanup suggestions (safe — only lists, no deletion)

## steps
- list_all
  command: git_branch_list

- list_merged
  command: git_branch_list
  input merged: main

- list_merged_master
  command: git_branch_list
  input merged: master

## triggers
- pattern: "清理分支" (weight: 1.0)
- pattern: "cleanup branches" (weight: 0.9)
- pattern: "删除已合并" (weight: 0.8)

## tags
- git
- compose
- safe
