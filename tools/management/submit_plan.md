# tool: submit_plan
domain: management
type: shell
description: Submit a structured multi-step plan for human review before execution. Use for multi-file refactors, architecture changes, or anything expensive to undo. The plan is persisted as markdown in .livingtree/plans/ and requires approval before tools execute. Adapted from DeepSeek-Reasonix plan mode.

## parameters
- title: string (required) — Short (~80 char) plan title
- body: string (required) — Markdown plan content with file-by-file breakdown, risks, and steps
- steps_json: string (default: "[]") — Optional JSON array of structured steps, each with: id, title, action, risk (low/med/high), targets (file paths array)

## command
$dir = Join-Path $env:LTAI_WORKSPACE ".livingtree\plans"
if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$safeTitle = "{{title}}" -replace '[^\w\-\.]', '_'
$file = Join-Path $dir "${timestamp}_$safeTitle.md"
$content = @"
# {{title}}
**Submitted:** $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
**Status:** pending_review

{{body}}

## Steps
```json
{{steps_json}}
```
"@
Set-Content -LiteralPath $file -Value $content -Encoding UTF8
Write-Output "Plan submitted: $file"
Write-Output "Status: PENDING REVIEW — plan will not execute until explicitly approved via /apply"

## triggers
- pattern: "submit plan" (weight: 1.0)
- pattern: "create plan" (weight: 0.9)
- pattern: "plan mode" (weight: 0.8)
- pattern: "/plan" (weight: 0.9)
- pattern: "提交计划" (weight: 0.7)

## tags
- management
- safe
- plan
- review-gate
