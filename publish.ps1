<#
.SYNOPSIS
    LTAI v7.0 统一发布 — 6 个自包含应用 → dist/ 根目录
.DESCRIPTION
    发布所有 exe 项目，将自包含单文件发布输出收集到 dist/ 根目录。
    每个 EXE 带上其运行时依赖 (native DLLs)，共享配置和模型文件。
.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Clean
#>
param([switch]$Clean)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$projects = @("LTAI.Cli", "LTAI.Host", "LTAI.MCP", "LTAI.TUI", "LTAI.WebApp", "LTAI.Desktop")

if ($Clean) {
    Write-Host "Cleaning dist/..." -ForegroundColor Yellow
    Get-ChildItem "dist" -Exclude ".gitkeep" -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== LTAI v7.0 Unified Publish ===" -ForegroundColor Cyan

$ok = 0
foreach ($p in $projects) {
    Write-Host "`n  Publishing $p..." -ForegroundColor Yellow
    & dotnet publish "src/$p/$p.csproj" -c Release --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED: $p" -ForegroundColor Red
        continue
    }
    $ok++
}

Write-Host "`n=== Flattening to dist/ ===" -ForegroundColor Cyan

# Find all self-contained publish directories and copy ALL files to dist/ root
$publishDirs = Get-ChildItem -Path "dist" -Recurse -Directory |
    Where-Object { $_.Name -eq "publish" -and $_.Parent.Name -eq "win-x64" }

foreach ($dir in $publishDirs) {
    $projectDir = $dir.Parent.Parent
    Write-Host "  Copying $($projectDir.Name)..." -ForegroundColor Green
    # For self-contained, the EXE needs the native DLLs in the same directory
    Copy-Item "$($dir.FullName)\*" "dist\" -Recurse -Force
}

# Copy shared config and models
$config = "src/LTAI.Host/appsettings.json"
if (Test-Path $config) { Copy-Item $config "dist/appsettings.json" -Force }

if (Test-Path "prompts") { Copy-Item "prompts" "dist/prompts" -Recurse -Force }

$modelDir = "models"
if (Test-Path $modelDir) {
    Copy-Item $modelDir "dist/models" -Recurse -Force
    Write-Host "  models/ → dist/" -ForegroundColor Green
}

Write-Host "`n=== dist/ contents ===" -ForegroundColor Cyan
Get-ChildItem "dist" -Filter "LTAI.*.exe" | ForEach-Object {
    $mb = [math]::Round($_.Length / 1MB, 1)
    Write-Host "  $($_.Name)  $mb MB"
}
$totalMB = [math]::Round((Get-ChildItem "dist" -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 0)
Write-Host "  Total: $totalMB MB"

Write-Host "`n=== Done: $ok/6 published ===" -ForegroundColor Cyan
