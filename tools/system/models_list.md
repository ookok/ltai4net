# tool: models_list
domain: system
type: service
description: List available AI models

## parameters

## service
name: ModelManager
method: ListAll

## triggers
- pattern: "models list" (weight: 1.0)
- pattern: "模型列表" (weight: 0.9)

## tags
- system
- safe
