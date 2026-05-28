# tool: edit_search_replace
domain: code
type: service
description: Replace code by exact text match (SEARCH/REPLACE). The SEARCH text must appear exactly once in the file — provide enough surrounding context to make it unique. This is safer than line-number editing because positions can shift. Adapted from DeepSeek-Reasonix edit mode.

## parameters
- path: string (required) — File path to modify
- search: string (required) — Exact text to find (whitespace-sensitive, must be unique)
- replace: string (required) — Text to substitute in place of search

## service
name: CodeEditTools
method: EditSearchReplace

## triggers
- pattern: "search replace" (weight: 1.0)
- pattern: "SEARCH/REPLACE" (weight: 1.0)
- pattern: "replace text" (weight: 0.8)
- pattern: "find and replace" (weight: 0.7)
- pattern: "内容替换" (weight: 0.6)

## tags
- code
- modify
- search-replace
