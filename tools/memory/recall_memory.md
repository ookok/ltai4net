# tool: recall_memory
domain: memory
type: shell
description: Recall a saved memory by its key. Returns the full stored content with metadata. Adapted from DeepSeek-Reasonix memory system.

## parameters
- key: string (required) — Memory key to recall

## command
$dir = Join-Path $env:LTAI_WORKSPACE ".livingtree\memories"
$file = Join-Path $dir "{{key}}.json"
if (Test-Path $file) {
    $content = Get-Content -LiteralPath $file -Raw -Encoding UTF8
    Write-Output $content
} else {
    Write-Output "{ `"error`": `"Memory '{{key}}' not found`" }"
}

## triggers
- pattern: "recall memory" (weight: 1.0)
- pattern: "recall" (weight: 0.8)
- pattern: "what did I save about" (weight: 0.7)
- pattern: "回忆" (weight: 0.6)

## tags
- memory
- safe
