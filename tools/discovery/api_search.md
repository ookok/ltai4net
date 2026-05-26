# tool: api_search
domain: discovery
type: service
description: Search API tools

## parameters
- query: string (required) — Search query

## service
name: ApiToolCatalog
method: Search

## triggers
- pattern: "api search" (weight: 1.0)
- pattern: "API搜索" (weight: 0.9)

## tags
- discovery
- safe
