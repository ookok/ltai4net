# tool: git_blame
domain: git
type: shell
description: Show line-by-line authorship information for a file

## parameters
- path: string (default: ".") — Repository path
- file: string (required) — File path relative to repo root
- line_start: int — Start line number
- line_end: int — End line number
- show_email: bool (default: false) — Show author email instead of name

## command
git -C {{path}} blame {{#if show_email}}-e{{/if}} {{#if line_start}}-L {{line_start}},{{line_end}}{{/if}} -- "{{file}}"

## triggers
- pattern: "git blame" (weight: 1.0)
- pattern: "谁写的这段" (weight: 0.9)
- pattern: "代码作者" (weight: 0.8)
- pattern: "blame" (weight: 0.7)

## tags
- git
- info
- safe
