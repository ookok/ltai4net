# tool: code_edit_validate
domain: code
type: service
description: Validate code syntax

## parameters
- path: string (required) — File path to validate

## service
name: CodeEditTools
method: EditValidateSyntax

## triggers
- pattern: "validate syntax" (weight: 1.0)
- pattern: "语法检查" (weight: 0.9)

## tags
- code
- safe
