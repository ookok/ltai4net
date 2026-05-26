# tool: classify_noise_level
domain: eia
type: service
description: Classify noise level per GB3096-2008 / 声环境质量分类（GB3096-2008）

## parameters
- daytime_db: double (required) — Daytime noise level dB / 昼间噪声值
- night_db: double (required) — Nighttime noise level dB / 夜间噪声值
- zone_category: string (default: "class2") — Acoustic zone category / 声功能区类别

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ClassifyNoise

## triggers
- pattern: "噪声等级" (weight: 1.0)
- pattern: "声环境分类" (weight: 0.9)
- pattern: "noise level classification" (weight: 0.9)
- pattern: "classify noise" (weight: 0.8)

## tags
- eia
- noise
- classification
- standard
