# tool: doc_text_extract
domain: doc
type: shell
description: Extract text from a file

## parameters
- path: string (required) — File path to extract text from

## command
if (Test-Path -LiteralPath "{{path}}") { Get-Content -LiteralPath "{{path}}" -Raw } else { Write-Output '{"error":"file not found"}' }

## triggers
- pattern: "extract text" (weight: 1.0)
- pattern: "提取文本" (weight: 0.9)
- pattern: "read document" (weight: 0.8)

## tags
- doc
- safe
