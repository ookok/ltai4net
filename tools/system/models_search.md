# tool: models_search
domain: system
type: service
description: Search available models

## parameters
- query: string (required) — Search query for models

## service
name: ModelManager
method: Search

## triggers
- pattern: "model search" (weight: 1.0)
- pattern: "搜索模型" (weight: 0.9)

## tags
- system
- safe
