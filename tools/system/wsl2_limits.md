# tool: wsl2_limits
domain: system
type: service
description: Set WSL2 resource limits

## parameters
- memory_mb: int (required) — Memory limit in megabytes
- processors: int (required) — Number of processors

## service
name: Wsl2Manager
method: SetResourceLimits

## triggers
- pattern: "wsl2 limits" (weight: 1.0)

## tags
- system
- modify
