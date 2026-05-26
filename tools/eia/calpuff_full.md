# tool: calpuff_full
domain: eia
type: shell
description: EPA CALPUFF non-steady-state air dispersion model (wrapper) / EPA CALPUFF非稳态大气扩散模型（包装器）

## parameters

## command
`echo "This tool requires C# wrapper execution"`

## triggers
- pattern: "CALPUFF" (weight: 1.0)
- pattern: "CALPUFF模型" (weight: 0.9)
- pattern: "EPA CALPUFF" (weight: 0.9)

## tags
- eia
- air
- long-range
- pro
