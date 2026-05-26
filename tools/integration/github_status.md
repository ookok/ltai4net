# tool: github_status
domain: integration
type: service
description: Check for updates from GitHub

## service
name: AutoUpdater
method: CheckForUpdatesAsync

## triggers
- pattern: "github update" (weight: 1.0)
- pattern: "检查更新" (weight: 0.9)

## tags
- integration
- safe
