# tool: gis_trip
domain: gis
type: compose
description: Plan a multi-stop trip with distance calculations between all stops

## steps
- geocode_all
  command: geocode
  input address: {{places}}
  input source: amap

- calc_segments (parallel)
  command: distance_calc
  input point1: $geocode_all
  input point2: $geocode_all
  input unit: meters

## parameters
- places: string (required) — Comma-separated list of places to visit

## triggers
- pattern: "trip|itinerary|tour|visit|行程|旅游|路线" (weight: 0.9)
- pattern: "plan.*day|multi.*stop" (weight: 0.8)

## tags
- gis
- spatial
- trip
