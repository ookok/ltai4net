# tool: vfs_search
domain: vfs
type: shell
description: Search files by name pattern

## parameters
- path: string (default: ".") — Directory path to search
- pattern: string (default: "*") — File name pattern

## command
Get-ChildItem -LiteralPath "{{path}}" -Filter "{{pattern}}" -Recurse -ErrorAction SilentlyContinue | Select-Object FullName | Format-Table -AutoSize -Wrap

## triggers
- pattern: "search files" (weight: 1.0)
- pattern: "搜索文件" (weight: 0.9)
- pattern: "find" (weight: 0.8)

## tags
- vfs
- safe
