# prompt: plan_user
domain: planning
description: Planner user prompt with tools list and query context
## triggers
plan user, task plan, planner user

## template
Available tools:
{tools}

User query: "{query}"

Output ONLY a JSON plan. Format: {{"plan":[{{"id":"s0","tool":"name","args":{{"p":"v"}},"deps":[]}}]}}
- Use "deps" to list step IDs that must complete before this step.
- Independent steps will run in parallel. Optional "id" field helps dependency tracking.
- Do NOT include tools that won't help answer the query.
- If no tools are needed, output: {{"plan":[]}}
