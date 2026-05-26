# tool: git_diff
domain: git
type: shell
description: Show changes between commits, branches, working tree, or index

## parameters
- path: string (default: ".") — Repository path
- from: string — Source commit/branch/ref (default: HEAD or staged)
- to: string — Target commit/branch/ref (default: unstaged working tree)
- name_only: bool (default: false) — Show only file names
- stat: bool (default: false) — Show diffstat instead of full diff
- staged: bool (default: false) — Show staged changes only

## command
git -C {{path}} diff {{#if name_only}}--name-only{{/if}} {{#if stat}}--stat{{/if}} {{#if staged}}--staged{{/if}} {{from}} {{to}}

## triggers
- pattern: "git diff" (weight: 1.0)
- pattern: "查看差异" (weight: 0.9)
- pattern: "哪些文件变了" (weight: 0.8)
- pattern: "代码差异" (weight: 0.8)

## tags
- git
- info
- safe
