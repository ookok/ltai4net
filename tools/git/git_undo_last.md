# tool: git_undo_last
domain: git
type: compose
description: Safely undo the last commit — keeps changes in working directory

## steps
- show_last
  command: git_show
  input ref: HEAD
  input stat: true

- reset_soft
  command: git_reset
  input mode: soft
  input target: HEAD~1

- show_status
  command: git_status
  input short: true

## triggers
- pattern: "撤销上次提交" (weight: 1.0)
- pattern: "undo commit" (weight: 0.9)
- pattern: "回退提交" (weight: 0.8)
- pattern: "撤销提交" (weight: 0.8)

## tags
- git
- compose
- safe
