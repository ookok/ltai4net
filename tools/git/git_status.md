# tool: git_status
domain: git
type: shell
description: Show the working tree status — changed files, staged files, untracked files, and branch info

## parameters
- path: string (default: ".") — Repository path
- short: bool (default: false) — Use short format

## command
git -C {{path}} status {{#if short}}--short{{/if}}

## triggers
- pattern: "git status" (weight: 1.0)
- pattern: "检查状态" (weight: 0.9)
- pattern: "有什么改动" (weight: 0.8)
- pattern: "working tree" (weight: 0.7)

## tags
- git
- info
- safe
