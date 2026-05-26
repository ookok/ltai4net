# tool: co2_equivalent
domain: eia
type: service
description: CO2 equivalent calculation (IPCC GWP100) / CO2当量计算（IPCC GWP100）

## parameters
- ch4_kg: double (required) — Methane mass kg / 甲烷质量
- n2o_kg: double (required) — Nitrous oxide mass kg / 氧化亚氮质量

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeCo2Equivalent

## triggers
- pattern: "CO2当量" (weight: 1.0)
- pattern: "碳排放当量" (weight: 0.9)
- pattern: "CO2 equivalent" (weight: 0.9)
- pattern: "GWP" (weight: 0.8)

## tags
- eia
- carbon
- climate
