# tool: gis_coordinate_transform
domain: gis
type: service
description: Transform between WGS84/GCJ02/CGCS2000 coordinate systems

## parameters
- lat: number (required) — Latitude
- lng: number (required) — Longitude
- from: string (required) — Source coordinate system (WGS84/GCJ02/CGCS2000)
- to: string (required) — Target coordinate system (WGS84/GCJ02/CGCS2000)

## service
LTAIToolRegistry.TransformCoord({{lat}}, {{lng}}, {{from}}, {{to}})

## triggers
- pattern: "coordinate" (weight: 1.0)
- pattern: "坐标转换" (weight: 0.9)
- pattern: "transform" (weight: 0.7)

## tags
- gis
- service
