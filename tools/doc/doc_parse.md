# tool: doc_doc_parse
domain: doc
type: shell
description: Parse document and extract content

## parameters
- path: string (required) — Document path to parse

## command
if (Test-Path -LiteralPath "{{path}}") { Get-Content -LiteralPath "{{path}}" -Raw } else { Write-Output '{"error":"file not found"}' }

## triggers
- pattern: "parse document" (weight: 1.0)
- pattern: "解析文档" (weight: 0.9)

## tags
- doc
- safe
