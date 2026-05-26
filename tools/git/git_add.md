# tool: git_add
domain: git
type: shell
description: Stage files for the next commit — supports glob patterns and interactive selection

## parameters
- path: string (default: ".") — Repository path
- files: string (required) — Files to stage (space-separated, supports glob: "*.cs" or "." for all)
- dry_run: bool (default: false) — Show what would be staged without actually staging

## command
git -C {{path}} add {{#if dry_run}}--dry-run{{/if}} {{files}}

## triggers
- pattern: "git add" (weight: 1.0)
- pattern: "暂存文件" (weight: 0.9)
- pattern: "stage files" (weight: 0.9)

## tags
- git
- modify
