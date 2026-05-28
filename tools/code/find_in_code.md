# tool: find_in_code
domain: code
type: service
description: Find all occurrences of an identifier in a source file, filtered by syntactic role (definition, call site, or reference). AST-aware — skips matches inside comments and strings. Adapted from DeepSeek-Reasonix code navigation.

## parameters
- path: string (required) — File path to search in
- name: string (required) — Exact identifier name to find
- kind: string (default: "any") — Filter by role: "any", "call", "definition", or "reference"

## service
name: CodeEditTools
method: ReadStructure

## triggers
- pattern: "find in code" (weight: 1.0)
- pattern: "usages of" (weight: 0.9)
- pattern: "who calls" (weight: 0.8)
- pattern: "where is .* defined" (weight: 0.8)
- pattern: "查找引用" (weight: 0.7)

## tags
- code
- safe
- search
- ast
