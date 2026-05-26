# tool: vfs_archive_create
domain: vfs
type: shell
description: Create a zip or tar.gz archive from files or directories

## parameters
- source: string (required) — Source file or directory path
- output: string (required) — Output archive path (.zip or .tar.gz)
- format: string (default: "zip") — Archive format: zip, tar, tar.gz

## command
if ("{{format}}" -eq "tar" -or "{{format}}" -eq "tar.gz") { tar -czf "{{output}}" -C (Split-Path "{{source}}" -Parent) (Split-Path "{{source}}" -Leaf) } else { Compress-Archive -Path "{{source}}" -DestinationPath "{{output}}" -Force }

## triggers
- pattern: "compress|zip|archive|打包|压缩" (weight: 0.9)
- pattern: "create (zip|archive|tar)" (weight: 0.8)

## tags
- vfs
- archive
- safe
