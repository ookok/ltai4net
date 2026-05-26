# tool: soil_erosion
domain: eia
type: service
description: Universal Soil Loss Equation (USLE) / 通用土壤流失方程

## parameters
- r_factor: double (required) — Rainfall erosivity factor / 降雨侵蚀力因子
- k_factor: double (required) — Soil erodibility factor / 土壤可蚀性因子
- ls_factor: double (required) — Slope length-gradient factor / 坡长坡度因子
- c_factor: double (required) — Cover management factor / 植被覆盖因子
- p_factor: double (required) — Support practice factor / 水土保持措施因子

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeSoilLoss

## triggers
- pattern: "土壤侵蚀" (weight: 1.0)
- pattern: "土壤流失" (weight: 0.9)
- pattern: "soil erosion" (weight: 0.9)
- pattern: "USLE" (weight: 0.8)

## tags
- eia
- soil
- erosion
