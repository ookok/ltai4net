# tool: vfs_info
domain: vfs
type: shell
description: Get file or directory metadata — type, size in bytes, last modified time (ISO 8601). Like `stat` on Unix.

## parameters
- path: string (required) — Path to inspect

## command
$f = Get-Item -LiteralPath "{{path}}" -Force -ErrorAction Stop
$type = if ($f.PSIsContainer) { "directory" } elseif ($f.LinkType) { "symlink" } else { "file" }
Write-Output "type: $type"
Write-Output "size: $($f.Length)"
Write-Output "mtime: $($f.LastWriteTimeUtc.ToString('o'))"
Write-Output "ctime: $($f.CreationTimeUtc.ToString('o'))"

## triggers
- pattern: "file info" (weight: 1.0)
- pattern: "stat" (weight: 0.9)
- pattern: "file size" (weight: 0.8)
- pattern: "文件信息" (weight: 0.7)
- pattern: "when was .* modified" (weight: 0.6)

## tags
- vfs
- safe
