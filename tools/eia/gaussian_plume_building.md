# tool: gaussian_plume_building
domain: eia
type: service
description: Gaussian plume with building downwash (Huber-Snyder, HJ2.2-2018) / 建筑物下洗高斯烟羽模型（Huber-Snyder, HJ2.2-2018）

## parameters
- q: double (required) — Emission rate g/s / 排放源强
- u: double (required) — Wind speed m/s / 风速
- h: double (required) — Effective height m / 有效源高
- x: double (required) — Downwind distance m / 下风向距离
- bh: double (required) — Building height m / 建筑物高度
- bw: double (required) — Building width m / 建筑物宽度

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeBuildingDownwash

## triggers
- pattern: "建筑物下洗" (weight: 1.0)
- pattern: "建筑下洗烟羽" (weight: 0.9)
- pattern: "building downwash" (weight: 0.9)
- pattern: "Huber Snyder" (weight: 0.8)

## tags
- eia
- air
- building
