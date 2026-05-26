# tool: vfs_archive_list
domain: vfs
type: shell
description: List contents of an archive file without extracting

## parameters
- archive: string (required) — Path to the archive file

## command
$ext = [IO.Path]::GetExtension("{{archive}}").ToLower(); if ($ext -eq ".zip") { Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead("{{archive}}").Entries | ForEach-Object { "$($_.FullName) ($($_.Length) bytes)" } } else { tar -tzf "{{archive}}" 2>$null; if ($LASTEXITCODE -ne 0) { tar -tf "{{archive}}" } }

## triggers
- pattern: "list (archive|zip|tar)" (weight: 0.8)
- pattern: "show contents of|what's in" (weight: 0.7)

## tags
- vfs
- archive
- safe
