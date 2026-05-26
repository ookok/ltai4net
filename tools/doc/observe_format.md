# tool: doc_observe_format
domain: doc
type: shell
description: Get file format info and stats

## parameters
- path: string (required) — File path to inspect

## command
if (Test-Path -LiteralPath "{{path}}") { $f = Get-Item -LiteralPath "{{path}}"; Write-Output "{""name"":""$($f.Name)"",""extension"":""$($f.Extension)"",""size"":$($f.Length),""modified"":""$($f.LastWriteTime.ToString('o'))""}" } else { Write-Output '{"error":"file not found"}' }

## triggers
- pattern: "file info" (weight: 1.0)
- pattern: "文件信息" (weight: 0.9)
- pattern: "format" (weight: 0.7)

## tags
- doc
- safe
