# tool: code_edit_insert
domain: code
type: service
description: Insert code after a line

## parameters
- path: string (required) — File path to modify
- line: int (required) — Line number to insert after
- code: string (required) — Code to insert

## service
name: CodeEditTools
method: EditInsertAfterLine

## triggers
- pattern: "insert code" (weight: 1.0)
- pattern: "插入代码" (weight: 0.9)

## tags
- code
- modify
