# tool: vfs_move
domain: vfs
type: shell
description: Move or rename file

## parameters
- source: string (required) — Source file path
- dest: string (required) — Destination file path

## command
Move-Item -LiteralPath "{{source}}" -Destination "{{dest}}" -Force

## triggers
- pattern: "move file" (weight: 1.0)
- pattern: "移动文件" (weight: 0.9)
- pattern: "rename" (weight: 0.8)

## tags
- vfs
- modify
