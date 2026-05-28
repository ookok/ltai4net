# tool: get_symbols
domain: code
type: service
description: Extract top-level and nested symbols (functions, classes, methods, interfaces, types, enums) from a source file via tree-sitter AST. Returns symbol names with 1-based line/column ranges. Grammar-aware — ignores matches inside comments and strings. Adapted from DeepSeek-Reasonix code navigation.

## parameters
- path: string (required) — File path to analyze

## service
name: CodeEditTools
method: ReadStructure

## triggers
- pattern: "get symbols" (weight: 1.0)
- pattern: "outline" (weight: 0.9)
- pattern: "symbols in" (weight: 0.8)
- pattern: "structure of" (weight: 0.8)
- pattern: "符号表" (weight: 0.7)
- pattern: "find functions in" (weight: 0.7)

## tags
- code
- safe
- symbols
- ast
