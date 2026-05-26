# tool: vfs_delete
domain: vfs
type: shell
description: Delete a file

## parameters
- path: string (required) — File path to delete

## command
Remove-Item -LiteralPath "{{path}}" -Force

## triggers
- pattern: "delete file" (weight: 1.0)
- pattern: "删除文件" (weight: 0.9)
- pattern: "rm" (weight: 0.7)

## tags
- vfs
- dangerous
