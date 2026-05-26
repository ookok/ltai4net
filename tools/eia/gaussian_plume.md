# tool: gaussian_plume
domain: eia
type: service
description: Gaussian plume air dispersion model per GB/T3840-1991 / 高斯烟羽大气扩散模型（GB/T3840-1991）

## parameters
- q: double (required) — Emission rate g/s / 排放源强
- u: double (required) — Wind speed m/s / 风速
- h: double (required) — Effective height m / 有效源高
- x: double (required) — Downwind distance m / 下风向距离

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeGaussianPlume

## triggers
- pattern: "高斯烟羽" (weight: 1.0)
- pattern: "大气扩散" (weight: 0.9)
- pattern: "Gaussian plume" (weight: 0.9)
- pattern: "air dispersion" (weight: 0.8)

## tags
- eia
- air
- model
