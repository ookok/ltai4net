# tool: memory_stats
domain: memory
type: shell
description: Get memory store statistics

## parameters

## command
$dir = ".livingtree/memories"; if (Test-Path $dir) { $files = Get-ChildItem -LiteralPath $dir -Filter "*.json"; $count = $files.Count; $size = ($files | Measure-Object -Property Length -Sum).Sum; Write-Output "{""count"":$count,""size_bytes"":$size}" } else { Write-Output '{"count":0,"size_bytes":0}' }

## triggers
- pattern: "memory stats" (weight: 1.0)
- pattern: "记忆统计" (weight: 0.9)

## tags
- memory
- safe
