# tool: vfs_exists
domain: vfs
type: shell
description: Check if file exists

## parameters
- path: string (required) — File path to check

## command
Test-Path -LiteralPath "{{path}}"

## triggers
- pattern: "file exists" (weight: 1.0)
- pattern: "文件存在" (weight: 0.9)

## tags
- vfs
- safe
