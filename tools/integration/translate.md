# tool: translate
domain: integration
type: service
description: Translate text between languages

## parameters
- text: string (required) — Text to translate
- from: string — Source language code
- to: string (required) — Target language code

## service
name: TranslateService
method: TranslateAsync

## triggers
- pattern: "translate" (weight: 1.0)
- pattern: "翻译" (weight: 0.9)

## tags
- integration
- safe
