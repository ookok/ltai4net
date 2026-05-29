# LTAI 发布脚本 — 自包含 JIT（目录发布）
param([string]$Platform = "win-x64")

Set-Location $PSScriptRoot
$outRoot = "dist/$Platform"

Write-Host "=== LTAI Publish ($Platform) ===" -ForegroundColor Cyan
Write-Host "Output: $outRoot/" -ForegroundColor Cyan
Write-Host ""

$ext = if ($Platform -like "win*") { ".exe" } else { "" }

$apps = @(
    @{ Name = "LTAI.Cli";    Dir = "cli";   Desc = "CLI" }
    @{ Name = "LTAI.TUI";    Dir = "tui";   Desc = "Terminal UI" }
    @{ Name = "LTAI.Desktop"; Dir = "desktop"; Desc = "Desktop" }
)

$publishArgs = @(
    "-c", "Release",
    "-r", $Platform,
    "--self-contained", "true",
    "-p:IncludeSymbols=false"
)

foreach ($app in $apps) {
    $outDir = "$outRoot/$($app.Dir)"
    $proj = "src/$($app.Name)/$($app.Name).csproj"
    
    Write-Host "  [$($app.Desc)] $($app.Name) ... " -NoNewline
    
    $output = & dotnet publish $proj @publishArgs -o $outDir 2>&1
    $hasError = $LASTEXITCODE -ne 0
    
    if (-not $hasError) {
        $exePath = "$outDir/$($app.Name)$ext"
        $size = if (Test-Path $exePath) { "{0:N0} KB" -f ((Get-Item $exePath).Length / 1KB) } else { "?" }
        Write-Host "OK ($size)" -ForegroundColor Green
    } else {
        Write-Host "FAIL" -ForegroundColor Red
        Write-Host $output
    }
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
