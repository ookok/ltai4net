# tool: code_edit_diff
domain: code
type: service
description: Diff between current code and snapshot

## parameters
- path: string (required) — File path to diff
- snapshot_id: string (required) — Snapshot identifier

## service
name: CodeEditTools
method: EditDiff

## triggers
- pattern: "edit diff" (weight: 1.0)
- pattern: "编辑差异" (weight: 0.9)

## tags
- code
- safe
