# tool: vfs_list
domain: vfs
type: shell
description: List directory contents

## parameters
- path: string (default: ".") — Directory path to list

## command
Get-ChildItem -LiteralPath "{{path}}" | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

## triggers
- pattern: "list files" (weight: 1.0)
- pattern: "列出文件" (weight: 0.9)
- pattern: "ls" (weight: 0.8)

## tags
- vfs
- safe
