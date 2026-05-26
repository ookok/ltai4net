# tool: ecological_risk
domain: eia
type: service
description: Multi-substance ecological risk index (Hakanson method) / 多物质生态风险指数（Hakanson方法）

## parameters
- metals_csv: string (required) — CSV with metal name and concentration columns / 含金属名称和浓度列的CSV

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: ComputeEcologicalRisk

## triggers
- pattern: "生态风险" (weight: 1.0)
- pattern: "潜在生态危害指数" (weight: 0.9)
- pattern: "ecological risk" (weight: 0.9)
- pattern: "Hakanson" (weight: 0.8)

## tags
- eia
- risk
- ecology
