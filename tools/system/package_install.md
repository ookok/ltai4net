# tool: package_install
domain: system
type: service
description: Install NuGet package

## parameters
- package_id: string (required) — NuGet package identifier
- version: string — Package version

## service
name: PkgManager
method: InstallNuGetAsync

## triggers
- pattern: "install package" (weight: 0.9)
- pattern: "安装包" (weight: 0.9)

## tags
- system
- modify
