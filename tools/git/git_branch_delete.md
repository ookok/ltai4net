# tool: git_branch_delete
domain: git
type: shell
description: Delete a local or remote branch. Use with caution!

## parameters
- path: string (default: ".") — Repository path
- name: string (required) — Branch name to delete
- force: bool (default: false) — Force delete even if not fully merged (DANGER)
- remote: bool (default: false) — Delete remote branch instead of local

## command
git -C {{path}} {{#if remote}}push origin --delete {{name}}{{/if}} {{#if not remote}}branch {{#if force}}-D{{/if}} {{#if not force}}-d{{/if}} {{name}}{{/if}}

## triggers
- pattern: "删除分支" (weight: 1.0)
- pattern: "git branch delete" (weight: 0.9)
- pattern: "cleanup branch" (weight: 0.7)

## tags
- git
- modify
- dangerous
