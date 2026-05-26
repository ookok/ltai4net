# tool: wsl2_list
domain: system
type: service
description: List WSL2 distros

## parameters

## service
name: Wsl2Manager
method: ListDistros

## triggers
- pattern: "wsl2 list" (weight: 1.0)
- pattern: "WSL列表" (weight: 0.9)

## tags
- system
- safe
