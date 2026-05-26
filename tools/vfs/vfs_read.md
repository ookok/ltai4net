# tool: vfs_read
domain: vfs
type: shell
description: Read file content

## parameters
- path: string (required) — File path to read

## command
Get-Content -LiteralPath "{{path}}" -Raw

## triggers
- pattern: "read file" (weight: 1.0)
- pattern: "读取文件" (weight: 0.9)

## tags
- vfs
- safe
