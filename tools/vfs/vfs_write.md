# tool: vfs_write
domain: vfs
type: shell
description: Write content to file

## parameters
- path: string (required) — File path to write
- content: string (required) — Content to write

## command
Set-Content -LiteralPath "{{path}}" -Value "{{content}}"

## triggers
- pattern: "write file" (weight: 1.0)
- pattern: "写入文件" (weight: 0.9)

## tags
- vfs
- modify
