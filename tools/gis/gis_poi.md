# tool: gis_poi
domain: gis
type: compose
description: Get detailed information about a place including coordinates, address, and attributes

## steps
- geocode
  command: geocode
  input address: {{place}}
  input source: amap

- reverse_lookup
  command: reverse_geocode
  input location: $geocode
  input source: amap

## parameters
- place: string (required) — Place name or address

## triggers
- pattern: "where is|tell me about|info|details|信息|在哪里" (weight: 0.9)
- pattern: "coordinates|location of|address of" (weight: 0.8)

## tags
- gis
- spatial
- poi
