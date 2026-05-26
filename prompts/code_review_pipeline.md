# prompt: code_review_pipeline
domain: code
description: Code review pipeline multi-step template

## template
Review the following code changes with these criteria:

Changes:
{{diff}}

Focus areas:
- Correctness: does the logic do what's intended?
- Maintainability: is the code clear and well-structured?
- Safety: are there security or data integrity concerns?
- Style: does it follow project conventions?
- Tests: are changes adequately covered?

For each issue found, report:
- File and line
- Severity (critical/high/medium/low)
- Description of the issue
- Suggested fix

## variables
- diff: The git diff or code changes to review (required)

## triggers
- pattern: "code review" (weight: 1.0)
- pattern: "review changes" (weight: 0.9)
- pattern: "代码审查" (weight: 1.0)

## tags
- code
- review
- pipeline
