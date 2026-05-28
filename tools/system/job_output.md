# tool: job_output
domain: system
type: shell
description: Read the latest output from a background job started with run_background.

## parameters
- job_id: int (required) — Job ID returned by run_background
- tail_lines: int (default: 80) — Number of recent output lines to return

## command
$job = Get-Job -Id {{job_id}} -ErrorAction Stop
$output = $job | Receive-Job
$lines = $output -split "`n"
$total = $lines.Count
$tail = if ($total -gt {{tail_lines}}) { $lines[-{{tail_lines}}..-1] } else { $lines }
Write-Output "job_id: {{job_id}}"
Write-Output "state: $($job.State)"
Write-Output "total_lines: $total"
Write-Output "--- output (last $($tail.Count) lines) ---"
$tail -join "`n"

## triggers
- pattern: "job output" (weight: 1.0)
- pattern: "check background" (weight: 0.8)
- pattern: "查看后台输出" (weight: 0.7)

## tags
- system
- safe
- background
