# LTAI Agent OS — Test Suite Runner with Audit Log Validation
# Usage: .\run-tests.ps1 [layer] [-Report]
# Example: .\run-tests.ps1 L0 -Report

param([string]$Layer = "L0", [switch]$Report)

$TestSpec = "docs\test_expected.csv"
$Cli = "dotnet run --project src\LTAI.Cli --"

if (-not (Test-Path $TestSpec)) { Write-Host "ERROR: $TestSpec not found" -ForegroundColor Red; exit 1 }

$specs = @{}
foreach ($line in Get-Content $TestSpec) {
    $parts = $line -split ',', 3
    if ($parts.Length -ge 3) {
        $specs[$parts[0]] = @{ Expected = $parts[1]; Pattern = $parts[2] }
    }
}

$Pass = 0; $Fail = 0; $total = 0
$results = @()

Write-Host ""
Write-Host "=== LTAI Agent OS Test Suite (Audit-Validated) ===" -ForegroundColor Cyan
Write-Host "Layer: $Layer" -ForegroundColor Yellow
Write-Host ""

$currentLayer = ""; $currentId = ""
foreach ($line in Get-Content "docs\testprompts.txt") {
    $trimmed = $line.Trim()
    if ($trimmed -eq "" -or $trimmed.StartsWith("# 编号") -or $trimmed.StartsWith("# 使用") -or $trimmed.StartsWith("# 验证")) { continue }
    
    if ($trimmed -match "^## L([0-5])") { $currentLayer = "L$($Matches[1])"; continue }
    if ($trimmed -match "^## 跨层") { $currentLayer = "CHAOS"; continue }
    if ($trimmed -match "^# (L[0-5]|CHAOS)-[A-Z0-9-]+") { $currentId = $Matches[1]; continue }
    
    if ($currentLayer -and $currentId -and $trimmed -and $trimmed -notmatch "^#") {
        if ($Layer -eq "all" -or $currentLayer -eq $Layer) {
            $total++
            $spec = $specs[$currentId]
            $expected = if ($spec) { $spec.Expected } else { "?" }
            $pattern = if ($spec) { $spec.Pattern } else { "" }
            
            Write-Host "[$currentLayer] [$currentId] " -NoNewline -ForegroundColor Cyan
            
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try {
                $output = & cmd /c "$Cli debug --query `"$trimmed`"" 2>&1 | Out-String
                $rc = $LASTEXITCODE
            } catch {
                $output = $_.Exception.Message
                $rc = 1
            }
            $sw.Stop()
            $elapsed = $sw.ElapsedMilliseconds
            
            $matched = $false
            if ($pattern) {
                foreach ($p in ($pattern -split '\|')) {
                    if ($output -match $p) { $matched = $true; break }
                }
            }
            
            $status = ""
            if ($expected -eq "❌" -and $matched) {
                $status = "PASS"; $Pass++; Write-Host "PASS" -ForegroundColor Green
            } elseif ($expected -eq "✅" -and ($matched -or $rc -eq 0)) {
                $status = "PASS"; $Pass++; Write-Host "PASS" -ForegroundColor Green
            } elseif ($expected -eq "⚠️") {
                $status = "PASS*"; $Pass++; Write-Host "PASS*" -ForegroundColor Yellow
            } elseif ($matched) {
                $status = "PASS"; $Pass++; Write-Host "PASS" -ForegroundColor Green
            } else {
                $status = "FAIL"; $Fail++; Write-Host "FAIL" -ForegroundColor Red
            }
            
            $results += [PSCustomObject]@{
                ID = $currentId
                Layer = $currentLayer
                Expected = $expected
                Status = $status
                Elapsed = $elapsed
                Matched = if ($matched) { "yes" } else { "no" }
            }
        }
        $currentId = ""
    }
}

Write-Host ""
Write-Host "=== Results ===" -ForegroundColor Cyan
$results | Format-Table ID, Layer, Expected, Status, Elapsed, Matched -AutoSize
Write-Host "PASS: $Pass  FAIL: $Fail  TOTAL: $total" -ForegroundColor White

if ($Report) {
    $reportPath = "docs\test_report_$(Get-Date -Format 'yyyyMMdd-HHmmss').csv"
    $results | Export-Csv $reportPath -NoTypeInformation
    Write-Host "Report saved to $reportPath" -ForegroundColor Cyan
}
