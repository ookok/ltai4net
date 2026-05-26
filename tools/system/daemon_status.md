# tool: daemon_status
domain: system
type: service
description: Check daemon service status

## parameters
- service_name: string (required) — Name of the daemon service

## service
name: DaemonManager
method: StatusAsync

## triggers
- pattern: "daemon" (weight: 1.0)
- pattern: "守护进程" (weight: 0.9)

## tags
- system
- safe
