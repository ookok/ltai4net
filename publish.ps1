<#
.SYNOPSIS
    LTAI v7.0 publish — 6 EXEs + shared files → dist/
.DESCRIPTION
    Sequential publish — each project outputs to dist/, overwriting shared runtime.
    No project subdirectories. One flat output directory.
.EXAMPLE
    .\publish.ps1
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "=== LTAI v7.0 Publish ===" -ForegroundColor Cyan

@("LTAI.Cli","LTAI.Host","LTAI.MCP","LTAI.TUI","LTAI.WebApp","LTAI.Desktop") | ForEach-Object {
    Write-Host "  $_ ... " -NoNewline
    & dotnet publish "src/$_/$_.csproj" -c Release -r win-x64 -o dist/ --self-contained 2>&1 > $null
    if ($LASTEXITCODE -eq 0) { Write-Host "OK" -ForegroundColor Green }
    else { Write-Host "FAIL" -ForegroundColor Red; throw $_ }
}

Get-ChildItem "dist" -Directory | Where-Object { $_.Name -match "^LTAI\.|^Cli$|^Host$|^MCP$|^TUI$|^WebApp$|^Desktop$|^publish$|^win-x64$" } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

@("src/LTAI.Host/appsettings.json","ltai.config.json","prompts","models") | ForEach-Object {
    if (Test-Path $_) { Copy-Item $_ dist/ -Recurse -Force }
}

Write-Host "`n=== dist/ ===" -ForegroundColor Cyan
Get-ChildItem dist -Filter "LTAI.*.exe" | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Green }
$total = [math]::Round((Get-ChildItem dist -Recurse -File | Measure-Object Length -Sum).Sum/1MB)
Write-Host "  Size: $total MB"
