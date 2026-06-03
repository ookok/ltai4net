param(
    [ValidateSet("TUI","Cli","Desktop","Web")]
    [string]$Project = "TUI",
    [switch]$Standalone,
    [switch]$Clean,
    [switch]$All
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot

if ($Clean) {
    Remove-Item -Recurse -Force "$root\dist\publish" -ErrorAction SilentlyContinue
}

$projects = if ($All) { @("TUI","Cli","Desktop","Web") } else { @($Project) }

# Step 1: Build all dependencies in order (Release ref assemblies needed for publish)
Write-Host "=== Build dependencies (Release) ===" -ForegroundColor Cyan
dotnet build "$root\src\LTAI.Core" -c Release
if ($LASTEXITCODE -ne 0) { throw "Core build failed" }
dotnet build "$root\src\LTAI.AI" -c Release
if ($LASTEXITCODE -ne 0) { throw "AI build failed" }
dotnet build "$root\src\LTAI.Agent" -c Release
if ($LASTEXITCODE -ne 0) { throw "Agent build failed" }

foreach ($name in $projects) {
    $srcName = $name
    $output = if ($Standalone) { "dist/publish/$name-standalone" } else { "dist/publish/$name" }

    Write-Host "=== Publish $name ===" -ForegroundColor Cyan
    if ($Standalone) {
        Write-Host "Self-contained single-file (slow)" -ForegroundColor Yellow
        dotnet publish "$root\src\LTAI.$srcName" -c Release -o "$root\$output" `
            --self-contained true -r win-x64 `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    } else {
        dotnet publish "$root\src\LTAI.$srcName" -c Release -o "$root\$output"
    }

    if ($LASTEXITCODE -ne 0) { throw "Publish $name failed" }

    $items = Get-ChildItem -Recurse "$root\$output" -File
    $totalSize = [math]::Round(($items | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host "  → $output ($($items.Count) files, ${totalSize}MB)" -ForegroundColor Green
}

Write-Host "=== All done ===" -ForegroundColor Green
