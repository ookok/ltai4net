# tool: streeter_phelps
domain: eia
type: service
description: Streeter-Phelps DO sag curve for water quality / 斯特里特-菲尔普斯溶解氧下垂曲线

## parameters
- do_sat: double (required) — Saturation DO mg/L / 饱和溶解氧
- do0: double (required) — Initial DO mg/L / 初始溶解氧
- k1: double (required) — Deoxygenation rate 1/day / 耗氧系数
- k2: double (required) — Reaeration rate 1/day / 复氧系数
- distance: double (required) — Distance km / 距离

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeStreeterPhelps

## triggers
- pattern: "溶解氧" (weight: 1.0)
- pattern: "氧垂曲线" (weight: 0.9)
- pattern: "Streeter Phelps" (weight: 0.9)
- pattern: "DO sag" (weight: 0.8)

## tags
- eia
- water
- model
