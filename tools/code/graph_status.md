# tool: code_graph_status
domain: code
type: service
description: Get code graph status

## parameters

## service
name: CodeGraphEnhanced
method: GetStatus

## triggers
- pattern: "code graph" (weight: 1.0)
- pattern: "代码图" (weight: 0.9)

## tags
- code
- safe
