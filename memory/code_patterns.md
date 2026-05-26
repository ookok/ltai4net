# memory: code_patterns
domain: code
confidence: 0.90
version: 1.0.0

## summary
Common code patterns, conventions, and best practices observed across the project.

## facts
- naming_convention: Methods use PascalCase, private fields use _camelCase prefix (confidence: 0.95)
- async_pattern: Async methods end with Async suffix and accept CancellationToken (confidence: 0.95)
- no_comments: Code style avoids comments unless explaining complex algorithm or business logic (confidence: 0.90)
- var_usage: var is used only when type is obvious from right-hand side (confidence: 0.85)
- nullable: Nullable reference types enabled — handle null explicitly (confidence: 0.90)

## context
These conventions are defined in AGENTS.md and followed throughout the LTAI project. New code should adhere to these patterns.

## tags
- code
- conventions
- patterns
- style

## triggers
- pattern: "code style" (weight: 1.0)
- pattern: "convention" (weight: 0.8)
- pattern: "code pattern" (weight: 0.9)
