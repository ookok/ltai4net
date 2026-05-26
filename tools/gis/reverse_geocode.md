# tool: gis_reverse_geocode
domain: gis
type: service
description: Convert coordinates to address

## parameters
- lng: float (required) — Longitude
- lat: float (required) — Latitude

## service
UnifiedMapService.ReverseGeocodeAsync({{lng}}, {{lat}})

## triggers
- pattern: "reverse geocode" (weight: 1.0)
- pattern: "逆地理编码" (weight: 0.9)
- pattern: "坐标转地址" (weight: 0.9)

## tags
- gis
- service
