# tool: git_pull
domain: git
type: shell
description: Fetch from and integrate with another repository or local branch

## parameters
- path: string (default: ".") — Repository path
- remote: string (default: "origin") — Remote name
- branch: string — Remote branch (default: current tracking branch)
- rebase: bool (default: false) — Rebase instead of merge

## command
git -C {{path}} pull {{#if rebase}}--rebase{{/if}} {{remote}} {{branch}}

## triggers
- pattern: "git pull" (weight: 1.0)
- pattern: "拉取代码" (weight: 0.9)
- pattern: "更新代码" (weight: 0.9)
- pattern: "同步远程" (weight: 0.8)

## tags
- git
- remote
