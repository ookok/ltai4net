# tool: git_checkout
domain: git
type: shell
description: Checkout a file, commit, or tag. Restore file from any ref.

## parameters
- path: string (default: ".") — Repository path
- target: string (required) — Branch, tag, commit hash, or file path to checkout
- files: string — Specific files to checkout (used with commit ref to restore specific files)

## command
git -C {{path}} checkout {{target}} {{#if files}}-- {{files}}{{/if}}

## triggers
- pattern: "git checkout" (weight: 1.0)
- pattern: "恢复文件" (weight: 0.9)
- pattern: "切换版本" (weight: 0.8)

## tags
- git
- modify
