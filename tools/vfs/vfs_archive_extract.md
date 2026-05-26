# tool: vfs_archive_extract
domain: vfs
type: shell
description: Extract a zip, tar.gz, tar, or 7z archive

## parameters
- archive: string (required) — Path to the archive file
- output_dir: string (default: ".") — Directory to extract into

## command
$ext = [IO.Path]::GetExtension("{{archive}}").ToLower(); if ($ext -eq ".zip") { Expand-Archive -Path "{{archive}}" -DestinationPath "{{output_dir}}" -Force } elseif ($ext -eq ".gz" -or $ext -eq ".tgz") { tar -xzf "{{archive}}" -C "{{output_dir}}" } elseif ($ext -eq ".tar") { tar -xf "{{archive}}" -C "{{output_dir}}" } else { Expand-Archive -Path "{{archive}}" -DestinationPath "{{output_dir}}" -Force }

## triggers
- pattern: "extract|unzip|解压|decompress" (weight: 0.9)
- pattern: "unpack archive" (weight: 0.8)

## tags
- vfs
- archive
- safe
