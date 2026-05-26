# tool: weather
domain: integration
type: service
description: Get current weather for a city

## parameters
- city: string (required) — City name
- source: string — Weather data provider

## service
name: WeatherService
method: GetWeatherAsync

## triggers
- pattern: "weather" (weight: 1.0)
- pattern: "天气" (weight: 0.9)

## tags
- integration
- safe
