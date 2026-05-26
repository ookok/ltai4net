# memory: git_workflow
domain: git
confidence: 0.90
version: 1.0.0

## summary
Git workflow conventions, commit message format, and merge strategies for the LTAI project.

## facts
- commit_format: Commits use English, present tense, max 72 chars — format: "type: brief description" (confidence: 0.95)
- commit_types: Valid types are fix/feat/refactor/perf/test/docs/chore (confidence: 0.95)
- pre_commit: Before committing, review with git diff --cached (confidence: 0.90)
- merge_prefer: Prefer git revert over manual rollback for code changes (confidence: 0.85)
- no_force: Never use force-push, skip hooks, or create empty commits without explicit request (confidence: 0.90)
- token_safety: Never commit API keys, tokens, or secrets to the repository (confidence: 0.95)

## context
These conventions are defined in AGENTS.md. The remote is at https://github.com/ookok/ltai4net on branch master.

## tags
- git
- workflow
- conventions
- commits

## triggers
- pattern: "git workflow" (weight: 1.0)
- pattern: "commit message" (weight: 0.8)
- pattern: "git convention" (weight: 0.9)
