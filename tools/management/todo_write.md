# tool: todo_write
domain: management
type: shell
description: Manage an in-session task tracker. Used by the Agent to track multi-step task progress. Each call replaces the entire list. Exactly one item may be in_progress at a time. Pass an empty list to clear. Adapted from DeepSeek-Reasonix task tracking.

## parameters
- todos_json: string (required) — JSON array of todo items, each with: content (string), status (one of: pending, in_progress, completed), activeForm (string, gerund form shown during progress). Example: '[{"content":"Add login page","status":"in_progress","activeForm":"Adding login page"},{"content":"Add tests","status":"pending","activeForm":"Adding tests"}]'

## command
$dir = Join-Path $env:LTAI_WORKSPACE ".livingtree"
if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$file = Join-Path $dir "todos.json"
$todos = '{{todos_json}}' | ConvertFrom-Json
$todos | ConvertTo-Json -Compress | Set-Content -LiteralPath $file -Encoding UTF8
$inProgress = ($todos | Where-Object { $_.status -eq "in_progress" }).content
$pending = ($todos | Where-Object { $_.status -eq "pending" }).Count
$done = ($todos | Where-Object { $_.status -eq "completed" }).Count
Write-Output "Todos updated: $done done, $pending pending$(if ($inProgress) { ", in_progress: $inProgress" } else { "" })"

## triggers
- pattern: "todo" (weight: 1.0)
- pattern: "task list" (weight: 0.8)
- pattern: "tracking" (weight: 0.7)
- pattern: "任务列表" (weight: 0.7)

## tags
- management
- safe
- tracking
