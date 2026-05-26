# tool: service
domain: core
type: service
description: Invoke any DI-registered service method by class name and method name — universal service dispatcher
timeout: 60

## service
name: ServiceDispatcher
method: Invoke

## parameters
- service_name: string (required) — Full class name or short name of the service type
- method_name: string (required) — Method name to invoke
- args_json: string — JSON object of method arguments (e.g. {"path":"src/", "count":10})

## triggers
- pattern: "service" (weight: 0.4)
- pattern: "invoke" (weight: 0.5)
- pattern: "调用服务" (weight: 0.6)

## tags
- core
- service
- universal
