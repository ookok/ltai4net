# tool: run_background
domain: system
type: shell
description: Start a long-running command in the background and return a job ID. Use with job_output, wait_for_job, stop_job, and list_jobs.

## parameters
- command: string (required) — Full shell command to run
- cwd: string (default: ".") — Working directory for the process
- wait_sec: int (default: 10) — Max seconds to wait for startup output before returning

## command
$job = Start-Job -ScriptBlock {
    param($cmd, $dir)
    Set-Location $dir
    Invoke-Expression $cmd 2>&1
} -ArgumentList "{{command}}", "{{cwd}}"
$job | Wait-Job -Timeout {{wait_sec}} | Out-Null
$output = $job | Receive-Job
Write-Output "job_id: $($job.Id)"
Write-Output "state: $($job.State)"
Write-Output "startup_output: $output"

## triggers
- pattern: "run background" (weight: 1.0)
- pattern: "start server" (weight: 0.9)
- pattern: "background process" (weight: 0.8)
- pattern: "后台运行" (weight: 0.7)

## tags
- system
- safe
- background
