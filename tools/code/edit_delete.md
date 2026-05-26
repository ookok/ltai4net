# tool: code_edit_delete
domain: code
type: service
description: Delete a range of lines

## parameters
- path: string (required) — File path to modify
- start_line: int (required) — Starting line number
- end_line: int (required) — Ending line number

## service
name: CodeEditTools
method: EditDeleteRange

## triggers
- pattern: "delete lines" (weight: 1.0)
- pattern: "删除行" (weight: 0.9)

## tags
- code
- modify
- dangerous
