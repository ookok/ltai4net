# LTAI V1.0 multi-platform publish
# Usage: .\publish.ps1 [-AOT] [-Platform win-x64|linux-x64|osx-arm64]
param([switch]$AOT, [string]$Platform = "win-x64")

Set-Location $PSScriptRoot

$mode = if ($AOT) { "AOT" } else { "JIT" }
Write-Host "=== LTAI V1.0 Publish ($mode, $Platform) ===" -ForegroundColor Cyan

$publishArgs = @("-c", "Release", "-r", $Platform, "--self-contained")
if ($AOT) { $publishArgs += "--property:PublishAot=true" }

@("LTAI.Cli","LTAI.Host","LTAI.MCP","LTAI.TUI","LTAI.WebApp") | ForEach-Object {
    Write-Host "  $_ ... " -NoNewline
    $dir = "dist/$Platform/$_"
    & dotnet publish "src/$_/$_.csproj" @publishArgs -o $dir 2>&1 > $null
    if ($LASTEXITCODE -eq 0) { Write-Host "OK" -ForegroundColor Green }
    else { Write-Host "FAIL (exit $LASTEXITCODE)" -ForegroundColor Red }
}

# Copy CLI to root dist
$ext = if ($Platform -like "win*") { ".exe" } else { "" }
Copy-Item "dist/$Platform/LTAI.Cli/ltai$ext" "dist/ltai-$Platform$ext" -Force -ErrorAction SilentlyContinue
Write-Host "Binary: dist/ltai-$Platform$ext" -ForegroundColor Cyan
