# tool: git_branch_create
domain: git
type: shell
description: Create a new branch, optionally switching to it

## parameters
- path: string (default: ".") — Repository path
- name: string (required) — New branch name
- switch: bool (default: true) — Switch to the new branch after creation
- from_ref: string — Create from this ref/branch instead of HEAD

## command
git -C {{path}} {{#if switch}}checkout -b{{/if}} {{#if not switch}}branch{{/if}} {{name}} {{from_ref}}

## triggers
- pattern: "创建分支" (weight: 1.0)
- pattern: "新建分支" (weight: 0.9)
- pattern: "git branch create" (weight: 0.9)

## tags
- git
- modify
