# tool: cli_list_tools
domain: cli
type: shell
description: List installed CLI tools

## parameters

## command
$dir = ".livingtree/cli_tools"; if (Test-Path $dir) { Get-ChildItem -LiteralPath $dir | Select-Object Name | Format-Table -AutoSize } else { Write-Output "no CLI tools installed" }

## triggers
- pattern: "cli tools" (weight: 1.0)
- pattern: "CLI工具" (weight: 0.9)
- pattern: "list tools" (weight: 0.8)

## tags
- cli
- safe
