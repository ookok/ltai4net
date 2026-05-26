# tool: river_mixing
domain: eia
type: service
description: River pollutant mixing zone calculation / 河流污染物混合区计算

## parameters
- flow_rate: double (required) — River flow rate m³/s / 河流流量
- width: double (required) — River width m / 河宽
- depth: double (required) — River depth m / 水深
- velocity: double (required) — Flow velocity m/s / 流速
- emission_load: double (required) — Pollutant emission load g/s / 污染物排放量

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeRiverMixing

## triggers
- pattern: "混合区" (weight: 1.0)
- pattern: "河流混合" (weight: 0.9)
- pattern: "river mixing" (weight: 0.9)
- pattern: "mixing zone" (weight: 0.8)

## tags
- eia
- water
- mixing
