# tool: shell_probe
domain: system
type: shell
description: Probe shell environment info

## command
`$os = if ($IsWindows) { "Windows" } elseif ($IsLinux) { "Linux" } elseif ($IsMacOS) { "macOS" } else { "Unknown" }; $ps = "$($PSVersionTable.PSVersion.Major).$($PSVersionTable.PSVersion.Minor)"; Write-Output "{""os"":""$os"",""shell"":""pwsh $ps"",""home"":""$env:USERPROFILE"",""pwd"":""$(Get-Location)""}"`

## triggers
- pattern: "shell info" (weight: 1.0)
- pattern: "shell环境" (weight: 0.9)

## tags
- system
- safe
