# tool: bash
domain: system
type: shell
description: Execute shell command directly

## parameters
- command: string (required) — Shell command to execute
- workdir: string — Working directory

## command
`{{command}}`

## triggers
- pattern: "shell" (weight: 1.0)
- pattern: "bash" (weight: 0.9)
- pattern: "execute" (weight: 0.8)

## tags
- system
- shell
