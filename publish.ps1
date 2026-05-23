<#
.SYNOPSIS
    LTAI v7.0 publish — each EXE to dist/{Project}/
.EXAMPLE
    .\publish.ps1
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "=== LTAI v7.0 Publish ===" -ForegroundColor Cyan

@("LTAI.Cli","LTAI.Host","LTAI.MCP","LTAI.TUI","LTAI.WebApp","LTAI.Desktop") | ForEach-Object {
    Write-Host "  $_ ... " -NoNewline
    $dir = "dist/$_"
    & dotnet publish "src/$_/$_.csproj" -c Release -r win-x64 -o $dir 2>&1 > $null
    if ($LASTEXITCODE -eq 0) { Write-Host "OK  → $dir/" -ForegroundColor Green }
    else { Write-Host "FAIL" -ForegroundColor Red; throw $_ }
}

Write-Host "`n=== dist/ ===" -ForegroundColor Cyan
Get-ChildItem dist -Directory | Where-Object { $_.Name -match "^LTAI\." } | ForEach-Object {
    $exe = Get-ChildItem $_.FullName -Filter "LTAI.*.exe" | Select-Object -First 1
    $mb = if ($exe) { [math]::Round($exe.Length/1MB,1) } else { 0 }
    Write-Host "  dist/$($_.Name)/  ($mb MB)" -ForegroundColor Green
}
Write-Host "`nDone." -ForegroundColor Cyan
