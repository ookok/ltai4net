# tool: models_show
domain: system
type: service
description: Show model details

## parameters
- name: string (required) — Model name to show details for

## service
name: ModelManager
method: Show

## triggers
- pattern: "model show" (weight: 1.0)
- pattern: "模型详情" (weight: 0.9)

## tags
- system
- safe
