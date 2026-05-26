# tool: git_tag_create
domain: git
type: shell
description: Create a tag (annotated or lightweight) at a specific commit

## parameters
- path: string (default: ".") — Repository path
- name: string (required) — Tag name (e.g. "v1.0.0")
- message: string — Annotation message (creates annotated tag if provided)
- ref: string (default: "HEAD") — Commit to tag
- push: bool (default: false) — Push the tag to remote after creation

## command
git -C {{path}} tag {{#if message}}-a {{name}} -m "{{message}}"{{/if}} {{#if not message}}{{name}}{{/if}} {{ref}} {{#if push}}&& git push origin {{name}}{{/if}}

## triggers
- pattern: "git tag" (weight: 1.0)
- pattern: "打标签" (weight: 0.9)
- pattern: "创建标签" (weight: 0.9)
- pattern: "发布版本" (weight: 0.8)

## tags
- git
- modify
