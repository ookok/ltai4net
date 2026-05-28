# tool: search_content
domain: code
type: shell
description: Recursively search file CONTENTS for a substring or regex pattern. Returns matching lines with file:line: text format. Supports glob filtering and context lines.

## parameters
- pattern: string (required) — Substring or regex to search for
- path: string (default: ".") — Directory to start search from
- glob: string (default: "*") — Filename filter (e.g. "*.cs", "*.md")
- case_sensitive: bool (default: false) — Enable case-sensitive matching
- context: int (default: 0) — Lines of context around each match (both sides, max 20)

## command
{{#if case_sensitive}}
rg --no-heading --line-number -C {{context}} --glob "{{glob}}" "{{pattern}}" {{path}}
{{else}}
rg --no-heading --line-number -C {{context}} --glob "{{glob}}" -i "{{pattern}}" {{path}}
{{/if}}

## triggers
- pattern: "search content" (weight: 1.0)
- pattern: "grep" (weight: 1.0)
- pattern: "搜索内容" (weight: 0.9)
- pattern: "find in files" (weight: 0.8)
- pattern: "search for" (weight: 0.8)
- pattern: "查找" (weight: 0.7)

## tags
- code
- safe
- grep
- search
