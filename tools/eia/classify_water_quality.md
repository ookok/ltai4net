# tool: classify_water_quality
domain: eia
type: service
description: Classify water quality per GB3838-2002 / 地表水质量分类（GB3838-2002）

## parameters
- cod: double (required) — COD mg/L / 化学需氧量
- bod: double (required) — BOD mg/L / 生化需氧量
- do_mg_l: double (required) — DO mg/L / 溶解氧
- nh3n: double (required) — NH3-N mg/L / 氨氮

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ClassifyWater

## triggers
- pattern: "水质分类" (weight: 1.0)
- pattern: "水质等级" (weight: 0.9)
- pattern: "water quality classification" (weight: 0.9)
- pattern: "classify water" (weight: 0.8)

## tags
- eia
- water
- classification
- standard
