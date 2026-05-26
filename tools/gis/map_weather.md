# tool: gis_map_weather
domain: gis
type: service
description: Get current weather for a city

## parameters
- city: string (required) — City name

## service
UnifiedMapService.GetWeatherAsync({{city}})

## triggers
- pattern: "weather" (weight: 1.0)
- pattern: "天气" (weight: 0.9)
- pattern: "天气预报" (weight: 0.9)

## tags
- gis
- service
