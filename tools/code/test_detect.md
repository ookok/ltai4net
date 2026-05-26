# tool: code_test_detect
domain: code
type: service
description: Detect test framework for project

## parameters
- path: string (required) — Project path to analyze

## service
name: TestHarness
method: DetectTestFramework

## triggers
- pattern: "detect test" (weight: 1.0)
- pattern: "检测测试" (weight: 0.9)

## tags
- code
- safe
