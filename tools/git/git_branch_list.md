# tool: git_branch_list
domain: git
type: shell
description: List local and remote branches with optional filtering

## parameters
- path: string (default: ".") — Repository path
- remote: bool (default: false) — Show remote branches
- all: bool (default: false) — Show both local and remote
- merged: string — Show branches merged into this branch
- no_merged: string — Show branches NOT merged into this branch

## command
git -C {{path}} branch {{#if remote}}-r{{/if}} {{#if all}}-a{{/if}} {{#if merged}}--merged {{merged}}{{/if}} {{#if no_merged}}--no-merged {{no_merged}}{{/if}}

## triggers
- pattern: "git branch" (weight: 1.0)
- pattern: "分支列表" (weight: 0.9)
- pattern: "查看分支" (weight: 0.9)
- pattern: "branch list" (weight: 0.8)

## tags
- git
- info
- safe
