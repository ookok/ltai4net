# tool: classify_air_quality
domain: eia
type: service
description: Classify air quality per GB3095-2012 / 环境空气质量分类（GB3095-2012）

## parameters
- so2: double (required) — SO2 concentration / 二氧化硫浓度
- no2: double (required) — NO2 concentration / 二氧化氮浓度
- pm10: double (required) — PM10 concentration / PM10浓度
- pm25: double (required) — PM2.5 concentration / PM2.5浓度

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ClassifyAir

## triggers
- pattern: "空气质量分类" (weight: 1.0)
- pattern: "空气质量等级" (weight: 0.9)
- pattern: "air quality classification" (weight: 0.9)
- pattern: "AQI" (weight: 0.8)

## tags
- eia
- air
- classification
- standard
