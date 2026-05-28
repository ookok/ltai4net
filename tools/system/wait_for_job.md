# tool: wait_for_job
domain: system
type: shell
description: Block until a background job completes or times out. Use for builds, installs, downloads started via run_background.

## parameters
- job_id: int (required) — Job ID returned by run_background
- timeout_sec: int (default: 300) — Max seconds to wait before returning

## command
$job = Get-Job -Id {{job_id}} -ErrorAction Stop
$completed = $job | Wait-Job -Timeout {{timeout_sec}}
$output = $job | Receive-Job
Write-Output "job_id: {{job_id}}"
Write-Output "completed: $($completed -ne $null)"
Write-Output "state: $($job.State)"
Write-Output "exit_code: $($job.ChildJobs[0].JobStateInfo.ExitCode)"
Write-Output "output: $output"

## triggers
- pattern: "wait for job" (weight: 1.0)
- pattern: "wait for build" (weight: 0.8)
- pattern: "等待后台完成" (weight: 0.7)

## tags
- system
- safe
- background
