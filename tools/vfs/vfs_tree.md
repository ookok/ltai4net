# tool: vfs_tree
domain: vfs
type: shell
description: Recursively list directory structure as an indented tree. Skips common dependency/build directories by default.

## parameters
- path: string (default: ".") — Root directory to tree from
- max_depth: int (default: 3) — Maximum recursion depth
- exclude: string (default: "node_modules|.git|bin|obj|dist|.vs|.idea|__pycache__|target|build|coverage") — Pipe-separated directory names to exclude

## command
{{#if max_depth}}
tree "{{path}}" -L {{max_depth}} -I "{{exclude}}" --charset utf-8 --dirsfirst 2>$null
{{else}}
tree "{{path}}" -I "{{exclude}}" --charset utf-8 --dirsfirst 2>$null
{{/if}}

## triggers
- pattern: "directory tree" (weight: 1.0)
- pattern: "tree" (weight: 0.9)
- pattern: "project structure" (weight: 0.9)
- pattern: "目录树" (weight: 0.8)
- pattern: "folder structure" (weight: 0.8)
- pattern: "show me the structure" (weight: 0.6)

## tags
- vfs
- safe
- overview
