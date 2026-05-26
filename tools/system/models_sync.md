# tool: models_sync
domain: system
type: service
description: Sync model info from remote

## parameters

## service
name: ModelManager
method: SyncInfo

## triggers
- pattern: "model sync" (weight: 1.0)
- pattern: "同步模型" (weight: 0.9)

## tags
- system
- modify
