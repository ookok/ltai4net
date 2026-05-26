# tool: memory_forget
domain: memory
type: shell
description: Delete a memory by key

## parameters
- key: string (required) — Memory key to delete

## command
$file = ".livingtree/memories/{{key}}.json"; if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force; Write-Output "deleted" } else { Write-Output "not_found" }

## triggers
- pattern: "forget" (weight: 1.0)
- pattern: "遗忘" (weight: 0.9)
- pattern: "delete memory" (weight: 0.9)

## tags
- memory
- modify
