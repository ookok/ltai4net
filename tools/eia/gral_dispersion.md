# tool: gral_dispersion
domain: eia
type: shell
description: GRAL Lagrangian particle dispersion model (wrapper) / GRAL拉格朗日粒子扩散模型（包装器）

## parameters

## command
`echo "This tool requires C# wrapper execution"`

## triggers
- pattern: "GRAL" (weight: 1.0)
- pattern: "GRAL模型" (weight: 0.9)
- pattern: "拉格朗日扩散" (weight: 0.9)
- pattern: "Lagrangian dispersion" (weight: 0.8)

## tags
- eia
- air
- lagrangian
- pro
