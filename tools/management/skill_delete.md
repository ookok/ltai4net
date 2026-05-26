# tool: skill_delete
domain: management
type: shell
description: Delete a skill by name

## parameters
- name: string (required) — Skill name to delete

## command
`$dir = ".livingtree/skills/{{name}}"; if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force; Write-Output "deleted" } else { Write-Output "not_found" }`

## triggers
- pattern: "skill delete" (weight: 1.0)
- pattern: "删除技能" (weight: 0.9)

## tags
- management
- dangerous
