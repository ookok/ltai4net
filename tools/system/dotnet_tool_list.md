# tool: dotnet_tool_list
domain: system
type: service
description: List installed .NET tools

## parameters

## service
name: PkgManager
method: GetInstalledToolsAsync

## triggers
- pattern: "dotnet tool list" (weight: 1.0)

## tags
- system
- safe
