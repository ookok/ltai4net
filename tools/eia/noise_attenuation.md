# tool: noise_attenuation
domain: eia
type: service
description: Simple noise attenuation with distance / 简单距离噪声衰减计算

## parameters
- lw: double (required) — Sound power level dB / 声功率级
- distance: double (required) — Distance m / 距离

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeNoiseAttenuation

## triggers
- pattern: "噪声衰减" (weight: 1.0)
- pattern: "距离衰减" (weight: 0.9)
- pattern: "noise attenuation" (weight: 0.9)
- pattern: "sound attenuation" (weight: 0.8)

## tags
- eia
- noise
- simple
