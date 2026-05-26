# tool: noise_traffic
domain: eia
type: service
description: Traffic noise prediction (FHWA/CJW method) / 交通噪声预测（FHWA/CJW方法）

## parameters
- volume_per_h: double (required) — Traffic volume vehicles/hour / 车流量
- speed_kmh: double (required) — Vehicle speed km/h / 车速
- distance: double (required) — Distance from road m / 距离
- heavy_ratio: double (required) — Heavy vehicle ratio / 大车比例

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeTrafficNoise

## triggers
- pattern: "交通噪声" (weight: 1.0)
- pattern: "公路噪声" (weight: 0.9)
- pattern: "traffic noise" (weight: 0.9)
- pattern: "FHWA" (weight: 0.8)

## tags
- eia
- noise
- traffic
