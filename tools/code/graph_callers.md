# tool: code_graph_callers
domain: code
type: service
description: Find callers of a symbol

## parameters
- symbol: string (required) — Symbol name to find callers for
- depth: int — Search depth

## service
name: CodeGraphEnhanced
method: GetCallers

## triggers
- pattern: "callers" (weight: 1.0)
- pattern: "调用者" (weight: 0.9)

## tags
- code
- safe
