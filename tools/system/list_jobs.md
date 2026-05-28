# tool: list_jobs
domain: system
type: shell
description: List all running and recently completed background jobs with their IDs, commands, and states.

## parameters
# (no parameters)

## command
$jobs = Get-Job
if ($jobs.Count -eq 0) {
    Write-Output "No background jobs."
} else {
    $jobs | ForEach-Object {
        $child = $_.ChildJobs[0]
        Write-Output "job_id: $($_.Id) | state: $($_.State) | command: $($child.Command) | started: $($_.PSBeginTime.ToString('HH:mm:ss'))"
    }
}

## triggers
- pattern: "list jobs" (weight: 1.0)
- pattern: "background jobs" (weight: 0.9)
- pattern: "running processes" (weight: 0.7)
- pattern: "后台任务" (weight: 0.7)

## tags
- system
- safe
- background
