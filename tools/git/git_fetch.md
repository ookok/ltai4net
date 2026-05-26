# tool: git_fetch
domain: git
type: shell
description: Download objects and refs from another repository (safe, no merge)

## parameters
- path: string (default: ".") — Repository path
- remote: string (default: "origin") — Remote name
- prune: bool (default: false) — Remove remote-tracking refs that no longer exist on remote
- all: bool (default: false) — Fetch all remotes

## command
git -C {{path}} fetch {{#if all}}--all{{/if}} {{#if prune}}--prune{{/if}} {{#if not all}}{{remote}}{{/if}}

## triggers
- pattern: "git fetch" (weight: 1.0)
- pattern: "获取远程" (weight: 0.9)
- pattern: "fetch remote" (weight: 0.8)

## tags
- git
- remote
- safe
