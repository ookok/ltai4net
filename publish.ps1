# LTAI v7.0 publish
# Usage: .\publish.ps1
Set-Location $PSScriptRoot

Write-Host "=== LTAI v7.0 Publish ===" -ForegroundColor Cyan

@("LTAI.Cli","LTAI.Host","LTAI.MCP","LTAI.TUI","LTAI.WebApp","LTAI.Desktop") | ForEach-Object {
    Write-Host "  $_ ... " -NoNewline
    $dir = "dist/$_"
    & dotnet publish "src/$_/$_.csproj" -c Release -r win-x64 -o $dir 2>&1 > $null
    if ($LASTEXITCODE -eq 0) { Write-Host "OK" -ForegroundColor Green }
    else { Write-Host "FAIL" -ForegroundColor Red }
}

# Clean SDK-generated short-name duplicates (Cli,Host,MCP,TUI,WebApp,Desktop)
Get-ChildItem dist -Directory | Where-Object { $_.Name -notmatch "^LTAI\.|^lib$" } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
