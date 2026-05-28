# tool: remember
domain: memory
type: shell
description: Save a memory for future sessions — preferences, corrections, project facts. Persisted as JSON in .livingtree/memories/. Loaded into context on next session start. Adapted from DeepSeek-Reasonix memory system.

## parameters
- key: string (required) — Short identifier for the memory (alphanumeric + underscores, max 40 chars)
- content: string (required) — The memory content to store
- category: string (default: "project") — Category: "user", "project", "feedback", or "reference"

## command
$dir = Join-Path $env:LTAI_WORKSPACE ".livingtree\memories"
if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$mem = @{
    key = "{{key}}"
    content = "{{content}}"
    category = "{{category}}"
    saved_at = [DateTime]::UtcNow.ToString("o")
} | ConvertTo-Json -Compress
$file = Join-Path $dir "{{key}}.json"
Set-Content -LiteralPath $file -Value $mem -Encoding UTF8
Write-Output "Memory '{{key}}' saved (category: {{category}})."

## triggers
- pattern: "remember" (weight: 1.0)
- pattern: "save memory" (weight: 0.9)
- pattern: "记住" (weight: 0.7)
- pattern: "save this for later" (weight: 0.6)

## tags
- memory
- safe
