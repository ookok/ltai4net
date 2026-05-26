# tool: gis_geocode
domain: gis
type: service
description: Convert address to coordinates

## parameters
- address: string (required) — Address to geocode

## service
UnifiedMapService.GeocodeAsync({{address}})

## triggers
- pattern: "geocode" (weight: 1.0)
- pattern: "地理编码" (weight: 0.9)
- pattern: "地址转坐标" (weight: 0.9)

## tags
- gis
- service
