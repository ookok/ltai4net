# tool: code_graph_blast_radius
domain: code
type: service
description: Calculate impact radius of code changes

## parameters
- symbol: string (required) — Symbol to calculate impact for
- max_depth: int — Maximum depth to traverse

## service
name: CodeGraphEnhanced
method: GetImpactRadius

## triggers
- pattern: "blast radius" (weight: 1.0)
- pattern: "影响范围" (weight: 0.9)

## tags
- code
- safe
