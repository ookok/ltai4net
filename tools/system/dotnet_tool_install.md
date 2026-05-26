# tool: dotnet_tool_install
domain: system
type: service
description: Install .NET CLI tool

## parameters
- tool_name: string (required) — Name of the .NET tool to install

## service
name: PkgManager
method: InstallDotnetToolAsync

## triggers
- pattern: "dotnet tool install" (weight: 1.0)
- pattern: "安装dotnet工具" (weight: 0.9)

## tags
- system
- modify
