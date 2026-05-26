# tool: gis_buffer
domain: gis
type: shell
description: Compute a buffer zone around a point and check if targets fall within it

## parameters
- center: string (required) — Center point name or coordinates
- radius: number (required) — Buffer radius in meters

## command
Write-Host "GIS Buffer: center={{center}}, radius={{radius}}m" -ForegroundColor Cyan

## triggers
- pattern: "buffer|within radius|radius check" (weight: 0.7)

## tags
- gis
- spatial
- buffer
