# tool: search_apis
domain: discovery
type: shell
description: Shell-based tool search

## parameters
- query: string (required) — Search query

## command
`Write-Output "{""query"":""{{query}}"",""results"":""Search via API catalog or tool registry for {{query}}""}"`

## triggers
- pattern: "search apis" (weight: 1.0)

## tags
- discovery
- safe
