# tool: service_status
domain: system
type: service
description: Manage system service status

## parameters
- action: string (required) — Service action to perform

## service
name: ServiceManager
method: StatusAsync

## triggers
- pattern: "service status" (weight: 1.0)
- pattern: "服务状态" (weight: 0.9)

## tags
- system
- safe
