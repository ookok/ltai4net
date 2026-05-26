# tool: resource_usage
domain: system
type: service
description: Get system resource usage stats

## parameters

## service
name: ResourceGuard
method: GetCurrentUsage

## triggers
- pattern: "resource usage" (weight: 1.0)
- pattern: "资源使用" (weight: 0.9)

## tags
- system
- safe
