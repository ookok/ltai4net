# tool: gis_route
domain: gis
type: compose
description: Compute route between two locations with distance and duration

## steps
- geocode_origin
  command: geocode
  input address: {{origin}}
  input source: amap

- geocode_dest
  command: geocode
  input address: {{destination}}
  input source: amap

- calc_distance
  command: distance_calc
  input point1: $geocode_origin
  input point2: $geocode_dest
  input unit: meters

## parameters
- origin: string (required) — Starting location
- destination: string (required) — Destination location

## triggers
- pattern: "route|direction|distance|how far|路线|距离" (weight: 0.9)
- pattern: "from.*to|how to get" (weight: 0.8)

## tags
- gis
- spatial
- route
