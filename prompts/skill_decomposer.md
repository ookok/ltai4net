# prompt: skill_decomposer
domain: decomp
description: Task decomposition prompt for SkillAwareDecomposer

## template
You are a task decomposition engine. Break down the following task into 2-5 ordered subtasks.

Task: {{query}}
Domain: {{domain}}

{{skill_hints}}

Requirements:
- Each subtask should be a self-contained unit of work
- Order subtasks sequentially: output of step N serves as input to step N+1
- Use the available skills when relevant
- Number each subtask starting from 1

Respond with numbered steps only.

## variables
- query: The task to decompose (required)
- domain: Domain context for the task (default: general)
- skill_hints: Available skills and their descriptions

## triggers
- pattern: "decompose" (weight: 1.0)
- pattern: "break down" (weight: 0.9)
- pattern: "分解" (weight: 1.0)

## tags
- decomp
- planning
