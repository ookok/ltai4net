# tool: code_read_class
domain: code
type: service
description: Read a class by name

## parameters
- path: string (required) — File path to read
- class_name: string (required) — Class name to read

## service
name: CodeEditTools
method: ReadClass

## triggers
- pattern: "read class" (weight: 1.0)
- pattern: "读取类" (weight: 0.9)

## tags
- code
- safe
