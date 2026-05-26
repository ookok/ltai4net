# tool: git_stash
domain: git
type: shell
description: Stash, list, apply, pop, or drop temporary changes

## parameters
- path: string (default: ".") — Repository path
- action: string (default: "push") — Action: push, pop, apply, list, drop, clear
- message: string — Description for the stash
- index: int — Stash index to pop/apply/drop (default: 0 for latest)

## command
git -C {{path}} stash {{action}} {{#if message}}-m "{{message}}"{{/if}} {{#if index}}stash@{{{index}}}{{/if}}

## triggers
- pattern: "git stash" (weight: 1.0)
- pattern: "暂存工作区" (weight: 0.9)
- pattern: "临时保存" (weight: 0.8)

## tags
- git
- modify
