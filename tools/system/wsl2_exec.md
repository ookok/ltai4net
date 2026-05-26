# tool: wsl2_exec
domain: system
type: service
description: Execute command in WSL2 distro

## parameters
- distro: string (required) — WSL2 distribution name
- command: string (required) — Command to execute

## service
name: Wsl2Manager
method: ExecuteInDistro

## triggers
- pattern: "wsl2 exec" (weight: 1.0)
- pattern: "WSL执行" (weight: 0.9)

## tags
- system
- modify
