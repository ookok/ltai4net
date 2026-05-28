<#
.SYNOPSIS
    LTAI 一键测试脚本 — Windows PowerShell
.DESCRIPTION
    构建 → 全量测试 → 覆盖率报告 → 汇总
.PARAMETER SkipCoverage
    跳过覆盖率收集（速度更快）
.PARAMETER RunLoadTest
    测试完成后运行 k6 负载测试
.EXAMPLE
    .\run-tests.ps1
    .\run-tests.ps1 -SkipCoverage
    .\run-tests.ps1 -RunLoadTest
#>

param(
    [switch]$SkipCoverage,
    [switch]$RunLoadTest
)

$ErrorActionPreference = "Stop"
$step = 0

function Next-Step($title) {
    $script:step++
    Write-Host "`n=== [$step/7] $title ===" -ForegroundColor Cyan
}

# ── 依赖检查 ──
try { $null = Get-Command dotnet -ErrorAction Stop }
catch { throw "缺少 dotnet CLI — https://dotnet.microsoft.com/download" }

if (!$SkipCoverage) {
    try { $null = Get-Command reportgenerator -ErrorAction Stop }
    catch { Write-Warning "缺少 reportgenerator — 运行: dotnet tool install -g dotnet-reportgenerator-globaltool" }
}

# ── 1. 清理 ──
Next-Step "清理上次构建"
if (Test-Path coverage-report) { Remove-Item -Recurse -Force coverage-report }
if (Test-Path TestResults)     { Remove-Item -Recurse -Force TestResults }
dotnet clean LTAI.sln -c Release -q 2>$null

# ── 2. 还原 ──
Next-Step "还原 NuGet 包"
dotnet restore LTAI.sln

# ── 3. 构建 ──
Next-Step "编译所有项目"
dotnet build LTAI.sln -c Release --no-restore -warnaserror

# ── 4. 运行测试 ──
Next-Step "运行全部测试（并行 xUnit）"
$coverageArg = if (!$SkipCoverage) { "--collect:`"XPlat Code Coverage`"" } else { "" }
$testResult = dotnet test LTAI.sln -c Release --no-build `
    --settings tests/runsettings.xml `
    $coverageArg `
    --logger "trx;LogFileName=test-results.trx" `
    -m:4

if ($LASTEXITCODE -ne 0) {
    Write-Host "⚠ 部分测试失败 — 检查上方详情" -ForegroundColor Red
}

# ── 5. 覆盖率报告 ──
if (!$SkipCoverage) {
    Next-Step "生成覆盖率报告"
    $coverageFiles = Get-ChildItem -Recurse -Filter "coverage.cobertura.xml" -Path tests
    if ($coverageFiles) {
        $reports = ($coverageFiles.FullName) -join ";"
        reportgenerator -reports:$reports -targetdir:coverage-report -reporttypes:Html
        Write-Host "✅ 覆盖率报告: $PWD\coverage-report\index.html" -ForegroundColor Green
    } else {
        Write-Warning "未找到覆盖率文件"
    }
}

# ── 6. 负载测试 ──
if ($RunLoadTest) {
    Next-Step "运行 k6 负载测试"
    try { $null = Get-Command k6 -ErrorAction Stop }
    catch { throw "缺少 k6 — https://k6.io/docs/get-started/installation/" }
    k6 run tests/loadtest/loadtest.js --vus 10 --duration 30s --summary-export=loadtest-summary.json
}

# ── 7. 汇总 ──
Next-Step "完成"
Write-Host "════════════════════════════════════════" -ForegroundColor Green
Write-Host "  全部完成"                               -ForegroundColor Green
Write-Host "  ⚡ 测试结果:  tests/**/TestResults/test-results.trx" -ForegroundColor Green
if (!$SkipCoverage -and (Test-Path coverage-report)) {
    Write-Host "  📊 覆盖率:    coverage-report/index.html" -ForegroundColor Green
}
if ($RunLoadTest) {
    Write-Host "  📈 负载报告:  loadtest-summary.json" -ForegroundColor Green
}
Write-Host "════════════════════════════════════════" -ForegroundColor Green
