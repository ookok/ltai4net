# tool: prompt
domain: core
type: prompt
description: Execute an LLM prompt with variable substitution — universal AI task tool
timeout: 120

## parameters
- message: string (required) — The prompt message to send to the AI

## template
{{message}}

## triggers
- pattern: "prompt" (weight: 0.4)
- pattern: "ai" (weight: 0.5)
- pattern: "分析" (weight: 0.6)
- pattern: "generate" (weight: 0.5)

## tags
- core
- prompt
- universal
