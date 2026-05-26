# tool: git_remote_list
domain: git
type: shell
description: List remote repositories with their URLs

## parameters
- path: string (default: ".") — Repository path
- verbose: bool (default: true) — Show URL and fetch/push details

## command
git -C {{path}} remote {{#if verbose}}-v{{/if}}

## triggers
- pattern: "git remote" (weight: 1.0)
- pattern: "远程仓库" (weight: 0.9)
- pattern: "remote list" (weight: 0.8)

## tags
- git
- info
- safe
