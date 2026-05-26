# tool: service_install
domain: system
type: service
description: Install LTAI as system service

## parameters

## service
name: ServiceManager
method: InstallAsync

## triggers
- pattern: "install service" (weight: 1.0)
- pattern: "安装服务" (weight: 0.9)

## tags
- system
- modify
