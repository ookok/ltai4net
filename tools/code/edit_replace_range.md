# tool: code_edit_replace_range
domain: code
type: service
description: Replace code in a line range

## parameters
- path: string (required) — File path to modify
- start_line: int (required) — Starting line number
- end_line: int (required) — Ending line number
- new_code: string (required) — New code to insert

## service
name: CodeEditTools
method: EditReplaceRange

## triggers
- pattern: "replace range" (weight: 1.0)
- pattern: "替换范围" (weight: 0.9)

## tags
- code
- modify
