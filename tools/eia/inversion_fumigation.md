# tool: inversion_fumigation
domain: eia
type: service
description: Inversion breakup fumigation model / 逆温破碎熏烟模型

## parameters
- q: double (required) — Emission rate g/s / 排放源强
- u: double (required) — Wind speed m/s / 风速
- h: double (required) — Effective height m / 有效源高
- x: double (required) — Downwind distance m / 下风向距离
- zi: double (required) — Inversion height m / 逆温层高度

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeFumigation

## triggers
- pattern: "逆温熏烟" (weight: 1.0)
- pattern: "黑烟" (weight: 0.9)
- pattern: "fumigation" (weight: 0.9)
- pattern: "inversion breakup" (weight: 0.8)

## tags
- eia
- air
- inversion
