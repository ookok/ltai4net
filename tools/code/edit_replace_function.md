# tool: code_edit_replace_function
domain: code
type: service
description: Replace a function body

## parameters
- path: string (required) — File path to modify
- function_name: string (required) — Function name to replace
- new_code: string (required) — New function code

## service
name: CodeEditTools
method: EditReplaceFunction

## triggers
- pattern: "replace function" (weight: 1.0)
- pattern: "替换函数" (weight: 0.9)

## tags
- code
- modify
