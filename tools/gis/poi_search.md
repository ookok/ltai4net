# tool: gis_poi_search
domain: gis
type: service
description: Search for Points of Interest

## parameters
- keyword: string (required) — Search keyword
- city: string — City name

## service
UnifiedMapService.SearchPOIAsync({{keyword}}, {{city}})

## triggers
- pattern: "poi" (weight: 1.0)
- pattern: "周边搜索" (weight: 0.9)
- pattern: "兴趣点" (weight: 0.9)

## tags
- gis
- service
