# tool: image_search
domain: integration
type: service
description: Search for images by query

## parameters
- query: string (required) — Search query
- count: int — Number of results to return
- source: string — Image source provider

## service
name: ImageSearchService
method: SearchAsync

## triggers
- pattern: "image search" (weight: 1.0)
- pattern: "图片搜索" (weight: 0.9)

## tags
- integration
- safe
