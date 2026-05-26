# tool: tool_stats
domain: management
type: service
description: Get tool usage statistics

## service
name: LTAIToolRegistry
method: GetToolStats

## triggers
- pattern: "tool stats" (weight: 1.0)
- pattern: "工具统计" (weight: 0.9)

## tags
- management
- safe
