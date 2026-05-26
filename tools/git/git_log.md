# tool: git_log
domain: git
type: shell
description: Show commit history with flexible formatting and filtering

## parameters
- path: string (default: ".") — Repository path
- count: int (default: 20) — Number of commits to show
- author: string — Filter by author name/email
- since: string — Show commits more recent than a specific date (e.g. "2024-01-01")
- until: string — Show commits older than a specific date
- grep: string — Search commit messages for pattern
- file: string — Show history for a specific file
- graph: bool (default: false) — Show branch graph visualization
- format: string (default: "medium") — Output format: oneline, short, medium, full

## command
git -C {{path}} log -{{count}} --format={{format}} {{#if author}}--author="{{author}}"{{/if}} {{#if since}}--since="{{since}}"{{/if}} {{#if until}}--until="{{until}}"{{/if}} {{#if grep}}--grep="{{grep}}"{{/if}} {{#if graph}}--graph --oneline --all{{/if}} {{#if file}}-- "{{file}}"{{/if}}

## triggers
- pattern: "git log" (weight: 1.0)
- pattern: "提交历史" (weight: 0.9)
- pattern: "commit history" (weight: 0.9)
- pattern: "最近改动" (weight: 0.8)
- pattern: "谁改的" (weight: 0.7)

## tags
- git
- info
- safe
