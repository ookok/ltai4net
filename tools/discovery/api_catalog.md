# tool: api_catalog
domain: discovery
type: service
description: Browse API tool catalog

## service
name: ApiToolCatalog
method: BuildPromptContext

## triggers
- pattern: "api catalog" (weight: 1.0)
- pattern: "API目录" (weight: 0.9)

## tags
- discovery
- safe
