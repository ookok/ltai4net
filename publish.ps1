# LTAI V1.0 multi-platform publish (single-file self-contained)
# Usage: .\publish.ps1 [-Platform win-x64|linux-x64|osx-arm64]
param([string]$Platform = "win-x64")

Set-Location $PSScriptRoot

Write-Host "=== LTAI V1.0 Publish (single-file, $Platform) ===" -ForegroundColor Cyan

$outDir = "dist/release/$Platform"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$publishArgs = @(
    "-c", "Release",
    "-r", $Platform,
    "-p:PublishSingleFile=true",
    "-p:SelfContained=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $outDir
)

@("LTAI.Cli","LTAI.Host","LTAI.MCP","LTAI.TUI","LTAI.WebApp") | ForEach-Object {
    Write-Host "  $_ ... " -NoNewline
    & dotnet publish "src/$_/$_.csproj" @publishArgs 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "OK" -ForegroundColor Green }
    else { Write-Host "FAIL (exit $LASTEXITCODE)" -ForegroundColor Red }
}

# Generate platform-specific CLI shortcut
$ext = if ($Platform -like "win*") { ".exe" } else { "" }
Copy-Item "$outDir/LTAI.Cli$ext" "dist/ltai-$Platform$ext" -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Output: $outDir/" -ForegroundColor Cyan
Get-ChildItem "$outDir/*$ext" | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 1)
    Write-Host "  $($_.Name)  ($sizeMB MB)" -ForegroundColor White
}
Write-Host ""
Write-Host "CLI binary: dist/ltai-$Platform$ext" -ForegroundColor Green
