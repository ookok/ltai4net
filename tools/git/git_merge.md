# tool: git_merge
domain: git
type: shell
description: Join two or more development histories together

## parameters
- path: string (default: ".") — Repository path
- branch: string (required) — Branch to merge into current branch
- no_ff: bool (default: false) — Create merge commit even if fast-forward possible
- squash: bool (default: false) — Squash all commits into one before merging
- abort: bool (default: false) — Abort the current in-progress merge

## command
git -C {{path}} merge {{#if abort}}--abort{{/if}} {{#if no_ff}}--no-ff{{/if}} {{#if squash}}--squash{{/if}} {{#if not abort}}{{branch}}{{/if}}

## triggers
- pattern: "git merge" (weight: 1.0)
- pattern: "合并分支" (weight: 0.9)
- pattern: "merge branch" (weight: 0.9)

## tags
- git
- modify
- dangerous
