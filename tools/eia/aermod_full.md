# tool: aermod_full
domain: eia
type: shell
description: EPA AERMOD regulatory air dispersion model (wrapper) / EPA AERMOD法规大气扩散模型（包装器）

## parameters

## command
`echo "This tool requires C# wrapper execution"`

## triggers
- pattern: "AERMOD" (weight: 1.0)
- pattern: "AERMOD模型" (weight: 0.9)
- pattern: "EPA AERMOD" (weight: 0.9)

## tags
- eia
- air
- regulatory
- pro
