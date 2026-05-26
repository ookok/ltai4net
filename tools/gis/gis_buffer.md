# tool: gis_gis_buffer
domain: gis
type: service
description: Create buffer polygon around point, return GeoJSON

## parameters
- lat: number (required) — Latitude
- lng: number (required) — Longitude
- radius_m: number (required) — Buffer radius in meters

## service
LTAIToolRegistry.ComputeBuffer({{lat}}, {{lng}}, {{radius_m}})

## triggers
- pattern: "buffer" (weight: 1.0)
- pattern: "缓冲区" (weight: 0.9)
- pattern: "polygon" (weight: 0.7)

## tags
- gis
- service
