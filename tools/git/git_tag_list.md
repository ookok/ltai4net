# tool: git_tag_list
domain: git
type: shell
description: List tags with optional pattern matching

## parameters
- path: string (default: ".") — Repository path
- pattern: string — Glob pattern to filter tags (e.g. "v1.*")
- sort: string (default: "-creatordate") — Sort key: -creatordate, version:refname, etc.

## command
git -C {{path}} tag --sort={{sort}} {{#if pattern}}--list "{{pattern}}"{{/if}}

## triggers
- pattern: "git tag" (weight: 1.0)
- pattern: "标签列表" (weight: 0.9)
- pattern: "版本列表" (weight: 0.8)

## tags
- git
- info
- safe
