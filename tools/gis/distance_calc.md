# tool: gis_distance_calc
domain: gis
type: service
description: Calculate Haversine distance between coordinates

## parameters
- lat1: number (required) — Latitude of point 1
- lng1: number (required) — Longitude of point 1
- lat2: number (required) — Latitude of point 2
- lng2: number (required) — Longitude of point 2

## service
LTAIToolRegistry.Haversine({{lat1}}, {{lng1}}, {{lat2}}, {{lng2}})

## triggers
- pattern: "distance" (weight: 1.0)
- pattern: "距离计算" (weight: 0.9)
- pattern: "haversine" (weight: 0.8)

## tags
- gis
- service
