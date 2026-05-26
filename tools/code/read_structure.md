# tool: code_read_structure
domain: code
type: service
description: Read file structure

## parameters
- path: string (required) — File path to read structure from

## service
name: CodeEditTools
method: ReadStructure

## triggers
- pattern: "file structure" (weight: 1.0)
- pattern: "文件结构" (weight: 0.9)

## tags
- code
- safe
