# tool: code_read_function
domain: code
type: service
description: Read a function by name

## parameters
- path: string (required) — File path to read
- function_name: string (required) — Function name to read

## service
name: CodeEditTools
method: ReadFunction

## triggers
- pattern: "read function" (weight: 1.0)
- pattern: "读取函数" (weight: 0.9)

## tags
- code
- safe
