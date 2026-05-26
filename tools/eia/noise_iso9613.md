# tool: noise_iso9613
domain: eia
type: service
description: ISO 9613-2 outdoor sound propagation model / ISO 9613-2户外声传播模型

## parameters
- lw: double (required) — Sound power level dB / 声功率级
- distance: double (required) — Distance m / 距离
- ground_type: string (default: "mixed") — Ground type / 地面类型

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeNoiseIso9613

## triggers
- pattern: "ISO 9613" (weight: 1.0)
- pattern: "声传播" (weight: 0.9)
- pattern: "噪声传播" (weight: 0.9)
- pattern: "sound propagation" (weight: 0.8)

## tags
- eia
- noise
- model
