# tool: lookup_standard
domain: eia
type: service
description: Look up Chinese environmental standard (GB/HJ) / 中国环境标准查询（GB/HJ）

## parameters
- code: string (required) — Standard code e.g. GB3095-2012 / 标准编号

## service
name: LTAI.Tools.Capability.Tools.LTAIToolRegistry
method: LookupStandard

## triggers
- pattern: "环境标准" (weight: 1.0)
- pattern: "标准查询" (weight: 0.9)
- pattern: "GB标准" (weight: 0.9)
- pattern: "HJ标准" (weight: 0.9)
- pattern: "environmental standard" (weight: 0.8)
- pattern: "lookup standard" (weight: 0.8)

## tags
- eia
- standard
- reference
