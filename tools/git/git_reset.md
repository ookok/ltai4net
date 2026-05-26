# tool: git_reset
domain: git
type: shell
description: Unstage files or reset branch to a previous state. WARNING: --hard will discard changes!

## parameters
- path: string (default: ".") — Repository path
- files: string — Files to unstage (leave empty to reset branch)
- mode: string (default: "mixed") — Reset mode: soft (keep changes staged), mixed (keep changes unstaged), hard (DISCARD changes!)
- target: string (default: "HEAD") — Target commit/ref (default HEAD for unstage, HEAD~1 to undo commit)

## command
git -C {{path}} reset {{#if mode}}--{{mode}}{{/if}} {{target}} {{#if files}}-- {{files}}{{/if}}

## triggers
- pattern: "git reset" (weight: 1.0)
- pattern: "撤销暂存" (weight: 0.9)
- pattern: "取消 add" (weight: 0.8)
- pattern: "回退版本" (weight: 0.8)

## tags
- git
- modify
- dangerous
