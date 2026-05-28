# LTAI 发布脚本 — 自包含 JIT（目录发布，非 single file）
# Usage: .\publish.ps1 [-Platform win-x64|linux-x64|osx-arm64]
param([string]$Platform = "win-x64")

Set-Location $PSScriptRoot

$outRoot = "dist/$Platform"
Write-Host "=== LTAI Publish (self-contained JIT, $Platform) ===" -ForegroundColor Cyan
Write-Host "Output: $outRoot/" -ForegroundColor Cyan
Write-Host ""

$ext = if ($Platform -like "win*") { ".exe" } else { "" }

$apps = @(
    @{ Name = "LTAI.Cli";    Dir = "";      Desc = "CLI" }
    @{ Name = "LTAI.TUI";    Dir = "tui";   Desc = "Terminal UI" }
    @{ Name = "LTAI.Host";   Dir = "host";  Desc = "Web Host" }
    @{ Name = "LTAI.MCP";    Dir = "mcp";   Desc = "MCP Server" }
)

$publishArgs = @(
    "-c", "Release",
    "-r", $Platform,
    "--self-contained", "true",
    "-p:SatelliteResourceLanguages=zh-Hans%3Ben",
    "-p:IncludeSymbols=false"
)

foreach ($app in $apps) {
    $outDir = if ($app.Dir -eq "") { $outRoot } else { "$outRoot/$($app.Dir)" }
    $proj = "src/$($app.Name)/$($app.Name).csproj"
    
    Write-Host "  [$($app.Desc)] $($app.Name) ... " -NoNewline
    
    & dotnet publish $proj @publishArgs -o $outDir *>&1 | Out-Null
    
    if ($LASTEXITCODE -eq 0) {
        $exePath = "$outDir/$($app.Name)$ext"
        $size = if (Test-Path $exePath) { "{0:N0} KB" -f ((Get-Item $exePath).Length / 1KB) } else { "?" }
        Write-Host "OK ($size)" -ForegroundColor Green
    } else {
        Write-Host "FAIL (exit $LASTEXITCODE)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
Write-Host "CLI:        $outRoot/LTAI.Cli$ext" -ForegroundColor Green
Write-Host "Web Host:   $outRoot/host/LTAI.Host$ext" -ForegroundColor Green
Write-Host "MCP Server: $outRoot/mcp/LTAI.MCP$ext" -ForegroundColor Green
