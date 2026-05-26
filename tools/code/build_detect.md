# tool: code_build_detect
domain: code
type: service
description: Detect build system for project

## parameters
- path: string (required) — Project path to analyze

## service
name: BuildPipeline
method: DetectBuildSystem

## triggers
- pattern: "detect build" (weight: 1.0)
- pattern: "检测构建" (weight: 0.9)

## tags
- code
- safe
