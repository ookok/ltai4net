# tool: git_commit
domain: git
type: shell
description: Record changes to the repository with a descriptive message

## parameters
- path: string (default: ".") — Repository path
- message: string (required) — Commit message
- amend: bool (default: false) — Amend the previous commit instead of creating a new one
- all: bool (default: false) — Automatically stage all modified and deleted files

## command
git -C {{path}} commit {{#if amend}}--amend --no-edit{{/if}} {{#if all}}-a{{/if}} -m "{{message}}"

## triggers
- pattern: "git commit" (weight: 1.0)
- pattern: "提交代码" (weight: 0.9)
- pattern: "commit" (weight: 0.8)

## tags
- git
- modify
