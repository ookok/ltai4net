# tool: gis_nearby
domain: gis
type: compose
description: Find nearby places around a location with optional category and radius filters

## steps
- geocode_ref
  command: geocode
  input address: {{reference}}
  input source: amap

- search_nearby
  command: poi_search
  input location: $geocode_ref
  input keyword: {{category}}
  input radius: {{radius}}

- filter_results (parallel)
  command: distance_calc
  input point1: $geocode_ref
  input point2: $search_nearby
  input unit: meters

## parameters
- reference: string (required) — Reference location name or address
- category: string (default: "restaurant") — Place category to search
- radius: number (default: 1000) — Search radius in meters

## triggers
- pattern: "nearby|near me|附近|周围|closest" (weight: 0.9)
- pattern: "find.*near|search.*around" (weight: 0.8)

## tags
- gis
- spatial
- nearby
