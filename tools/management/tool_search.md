# tool: tool_search
domain: management
type: service
description: Search available tools

## parameters
- query: string (required) — Search query
- category: string — Tool category filter

## service
name: LTAIToolRegistry
method: SearchTools

## triggers
- pattern: "tool search" (weight: 1.0)
- pattern: "搜索工具" (weight: 0.9)

## tags
- management
- safe
