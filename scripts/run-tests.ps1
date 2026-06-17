#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run LTAI tests with sensible defaults.

.DESCRIPTION
    Runs all LTAI tests, skipping integration tests (real network/API calls)
    by default. Pass -Integration to include them.

.PARAMETER Integration
    Include integration tests (real HTTP calls to external APIs).
.PARAMETER Filter
    Custom dotnet test filter expression.
.PARAMETER Project
    Specific test project path.
#>

param(
    [switch]$Integration,
    [string]$Filter,
    [string]$Project = "tests/LTAI.Tests"
)

$ErrorActionPreference = "Stop"

if ($Integration) {
    Write-Host "🧪 Running ALL tests (including integration)..." -ForegroundColor Yellow
    & dotnet test $Project --configuration Release --no-restore -v n
} elseif ($Filter) {
    Write-Host "🧪 Running tests with filter: $Filter" -ForegroundColor Cyan
    & dotnet test $Project --configuration Release --no-restore --filter $Filter -v n
} else {
    Write-Host "🧪 Running unit tests only (skip integration)..." -ForegroundColor Green
    Write-Host "   Tip: use -Integration to include real API/network tests" -ForegroundColor DarkGray
    & dotnet test $Project --configuration Release --no-restore --filter "Category!=Integration" -v n
}

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ All tests passed!" -ForegroundColor Green
} else {
    Write-Host "❌ Some tests failed." -ForegroundColor Red
    exit $LASTEXITCODE
}
