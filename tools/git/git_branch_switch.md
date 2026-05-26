# tool: git_branch_switch
domain: git
type: shell
description: Switch to an existing branch

## parameters
- path: string (default: ".") — Repository path
- name: string (required) — Branch name to switch to

## command
git -C {{path}} checkout {{name}}

## triggers
- pattern: "切换分支" (weight: 1.0)
- pattern: "git checkout" (weight: 0.9)
- pattern: "switch branch" (weight: 0.9)

## tags
- git
- modify
