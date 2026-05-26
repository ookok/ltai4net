# tool: hazard_quotient
domain: eia
type: service
description: Ecological Hazard Quotient for single substance / 单一物质生态危害商值

## parameters
- exposure: double (required) — Exposure level / 暴露水平
- reference_dose: double (required) — Reference dose / 参考剂量

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeHazardQuotient

## triggers
- pattern: "危害商值" (weight: 1.0)
- pattern: "风险商" (weight: 0.9)
- pattern: "hazard quotient" (weight: 0.9)
- pattern: "HQ" (weight: 0.8)

## tags
- eia
- risk
- toxicology
