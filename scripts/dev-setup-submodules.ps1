# LTAI 开发环境子模块设置脚本 (Windows)
# 用法: powershell -ExecutionPolicy Bypass -File scripts\dev-setup-submodules.ps1
# 作用: 初始化 + sparse-checkout extern/agent-framework (~251MB -> ~27MB)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$AgentFwPath = Join-Path $RepoRoot "extern\agent-framework"

Write-Host "=== LTAI 子模块设置 ===" -ForegroundColor Green
Write-Host "Repo: $RepoRoot"
Write-Host "Submodules:"
Write-Host "  - extern/agent-framework      (Microsoft.Agents.AI 全部 dotnet 项目)"
Write-Host "  - extern/durabletask-dotnet   (Microsoft.DurableTask.* 1.24.2 源码, 调试用)"
Write-Host ""

# 1. 初始化所有子模块
Write-Host "[1/4] git submodule update --init --recursive ..." -ForegroundColor Yellow
& git -C $RepoRoot submodule update --init --recursive
if ($LASTEXITCODE -ne 0) { throw "submodule init failed" }

# 2. 检测 agent-framework 是否存在
if (-not (Test-Path -LiteralPath (Join-Path $AgentFwPath ".git"))) {
    Write-Host "  [error] extern/agent-framework 不可用，跳过 sparse-checkout" -ForegroundColor Red
    exit 1
}

# 3. 应用 sparse-checkout
Write-Host "[2/4] 应用 MAF sparse-checkout (~251MB -> ~27MB) ..." -ForegroundColor Yellow
& git -C $AgentFwPath config core.sparseCheckout true
& git -C $AgentFwPath config core.sparseCheckoutCone false

$Patterns = @'
/*
!/dotnet/tests/
!/dotnet/samples/
!/dotnet/.github/
!/dotnet/.vscode/
!**/bin/
!**/obj/
!**/*.dll
!**/*.pdb
!**/*.cache
!**/*.cache.json
!**/*.nupkg
!**/*.nupkg.gz
!**/*.nuspec
'@

$InfoDir = Join-Path $AgentFwPath ".git\info"
if (-not (Test-Path -LiteralPath $InfoDir)) { New-Item -ItemType Directory -Path $InfoDir -Force | Out-Null }
$PatternFile = Join-Path $InfoDir "sparse-checkout"
Set-Content -LiteralPath $PatternFile -Value $Patterns -Encoding UTF8 -NoNewline

# 4. 重新 read-tree
Write-Host "[3/4] git read-tree -mu HEAD ..." -ForegroundColor Yellow
& git -C $AgentFwPath read-tree -mu HEAD 2>&1 | Out-Null
& git -C $AgentFwPath sparse-checkout reapply 2>&1 | Out-Null

# 5. 报告 size
Write-Host "[4/4] 验证 ..." -ForegroundColor Yellow
$SizeBytes = (Get-ChildItem -LiteralPath $AgentFwPath -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
$SizeMB = [math]::Round($SizeBytes / 1MB, 1)
Write-Host ""
Write-Host "  ✅ extern/agent-framework 当前占用: $SizeMB MB" -ForegroundColor Green
Write-Host ""
Write-Host "提示: 'dotnet build LTAI.sln' 验证编译。" -ForegroundColor Cyan
Write-Host "提示: 恢复完整 clone 执行 'git -C extern/agent-framework sparse-checkout disable && git -C extern/agent-framework checkout HEAD -- .'" -ForegroundColor Gray
