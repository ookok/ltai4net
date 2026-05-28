# tool: stop_job
domain: system
type: shell
description: Stop a running background job. Sends SIGTERM (Stop-Job) first, then SIGKILL (Remove-Job -Force) if still running.

## parameters
- job_id: int (required) — Job ID to stop

## command
$job = Get-Job -Id {{job_id}} -ErrorAction Stop
Stop-Job -Id {{job_id}}
$output = $job | Receive-Job
Remove-Job -Id {{job_id}} -Force
Write-Output "job_id: {{job_id}}"
Write-Output "stopped: true"
Write-Output "final_output: $output"

## triggers
- pattern: "stop job" (weight: 1.0)
- pattern: "kill background" (weight: 0.8)
- pattern: "停止后台" (weight: 0.7)

## tags
- system
- safe
- background
