# tool: code_read_range
domain: code
type: service
description: Read a range of lines

## parameters
- path: string (required) — File path to read
- start_line: int (required) — Starting line number
- count: int (required) — Number of lines to read

## service
name: CodeEditTools
method: ReadRange

## triggers
- pattern: "read lines" (weight: 1.0)
- pattern: "读取行" (weight: 0.9)

## tags
- code
- safe
