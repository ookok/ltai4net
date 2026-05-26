# tool: git_rebase
domain: git
type: shell
description: Reapply commits on top of another base tip

## parameters
- path: string (default: ".") — Repository path
- onto: string — Branch to rebase onto
- interactive: bool (default: false) — Interactive rebase (NOT for non-interactive use by AI)
- abort: bool (default: false) — Abort an in-progress rebase
- continue: bool (default: false) — Continue after resolving conflicts

## command
git -C {{path}} rebase {{#if abort}}--abort{{/if}} {{#if continue}}--continue{{/if}} {{#if not abort}}{{#if not continue}}{{onto}}{{/if}}{{/if}}

## triggers
- pattern: "git rebase" (weight: 1.0)
- pattern: "变基" (weight: 0.9)
- pattern: "rebase onto" (weight: 0.9)

## tags
- git
- modify
- dangerous
