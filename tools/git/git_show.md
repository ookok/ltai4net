# tool: git_show
domain: git
type: shell
description: Show details of a specific commit, tag, or ref — diff, message, author

## parameters
- path: string (default: ".") — Repository path
- ref: string (required) — Commit hash, tag name, or ref (e.g. HEAD, HEAD~1, branch-name)
- stat: bool (default: false) — Show only diffstat instead of full diff
- name_only: bool (default: false) — Show only changed file names

## command
git -C {{path}} show {{#if stat}}--stat{{/if}} {{#if name_only}}--name-only{{/if}} {{ref}}

## triggers
- pattern: "git show" (weight: 1.0)
- pattern: "查看提交" (weight: 0.9)
- pattern: "commit detail" (weight: 0.8)

## tags
- git
- info
- safe
