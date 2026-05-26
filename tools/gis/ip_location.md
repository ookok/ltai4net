# tool: gis_ip_location
domain: gis
type: service
description: Get geographic location of IP address

## parameters
- ip: string (required) — IP address to locate

## service
UnifiedMapService.GetIPLocationAsync({{ip}})

## triggers
- pattern: "ip location" (weight: 1.0)
- pattern: "IP定位" (weight: 0.9)
- pattern: "IP归属地" (weight: 0.9)

## tags
- gis
- service
