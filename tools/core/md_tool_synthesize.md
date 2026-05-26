# tool: md_tool_synthesize
domain: discovery
type: prompt
description: Generate markdown tool files from natural language descriptions using LLM

## template
You are a tool designer. Generate a complete .md tool file for: {{description}}
Domain: {{domain}}
{{#if preferred_type}}Preferred type: {{preferred_type}}{{/if}}

Follow the LTAI .md tool format exactly with # tool: header, ## parameters, ## command/service, ## triggers, ## tags sections.
Return ONLY the markdown block between ```md and ```.

## parameters
- description: string (required) — Natural language description of what the tool should do
- domain: string (default: general) — Tool domain category
- preferred_type: string (default: shell) — shell|http|compose|prompt|service

## triggers
- pattern: "生成工具" (weight: 1.0)
- pattern: "generate tool" (weight: 0.9)
- pattern: "创建工具" (weight: 0.9)

## tags
- discovery
- synthesis
- meta
