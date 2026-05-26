# tool: shell
domain: core
type: shell
description: Execute any shell command — the universal tool for CLI, file operations, git, system probes, and more
timeout: 120
max_output_lines: 100

## parameters
- command: string (required) — Shell command to execute
- workdir: string (default: ".") — Working directory

## command
{{command}}

## triggers
- pattern: "execute" (weight: 0.5)
- pattern: "run command" (weight: 0.7)
- pattern: "shell" (weight: 0.6)

## tags
- core
- shell
- universal
