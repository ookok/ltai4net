# tool: git_push
domain: git
type: shell
description: Update remote refs along with associated objects

## parameters
- path: string (default: ".") — Repository path
- remote: string (default: "origin") — Remote name
- branch: string — Branch to push (default: current branch)
- force: bool (default: false) — Force push (DANGER: overwrites remote history!)
- set_upstream: bool (default: false) — Set upstream tracking for new branches
- tags: bool (default: false) — Push tags as well

## command
git -C {{path}} push {{#if set_upstream}}-u {{remote}} {{branch}}{{/if}} {{#if force}}--force{{/if}} {{#if tags}}--tags{{/if}} {{#if not set_upstream}}{{remote}} {{branch}}{{/if}}

## triggers
- pattern: "git push" (weight: 1.0)
- pattern: "推送代码" (weight: 0.9)
- pattern: "上传代码" (weight: 0.8)

## tags
- git
- remote
- dangerous
