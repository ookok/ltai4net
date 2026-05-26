# tool: service_uninstall
domain: system
type: service
description: Uninstall LTAI system service

## parameters

## service
name: ServiceManager
method: UninstallAsync

## triggers
- pattern: "uninstall service" (weight: 1.0)
- pattern: "卸载服务" (weight: 0.9)

## tags
- system
- modify
