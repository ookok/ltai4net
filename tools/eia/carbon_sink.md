# tool: carbon_sink
domain: eia
type: service
description: Forest/grassland carbon sink estimation / 森林草地碳汇估算

## parameters
- area_ha: double (required) — Area in hectares / 面积（公顷）
- vegetation_type: string (required) — Vegetation type / 植被类型
- growth_rate: double (required) — Growth rate tC/ha/yr / 固碳速率

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeCarbonSink

## triggers
- pattern: "碳汇" (weight: 1.0)
- pattern: "碳吸收" (weight: 0.9)
- pattern: "carbon sink" (weight: 0.9)
- pattern: "固碳" (weight: 0.9)

## tags
- eia
- carbon
- ecology
