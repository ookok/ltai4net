#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Cloudflare WARP 一键安装脚本
.DESCRIPTION
    自动下载并静默安装 Cloudflare WARP 客户端 (Windows)。
    安装后 warp-cli 自动加入 PATH，LTAI Accelerator 可自动识别和使用。
.LINK
    https://downloads.cloudflareclient.com/v1/download/windows/ga
#>

$ErrorActionPreference = "Stop"

# ── 检测是否已安装 ──────────────────────────────────────────────
$existing = Get-Command "warp-cli" -ErrorAction SilentlyContinue
if ($existing)
{
    Write-Host "✅ Cloudflare WARP 已安装。路径: $($existing.Source)" -ForegroundColor Green
    warp-cli --version 2>$null
    exit 0
}

# ── 下载 URL（保持与 MainWindow.axaml.cs WarpMirrorUrl 常量同步）──
$urls = @(
    "https://downloads.cloudflareclient.com/v1/download/windows/ga",
    "https://github.com/cloudflare/cloudflare-warp/releases/download/2026.4.1390.0/Cloudflare_WARP_2026.4.1390.0.msi"
)
$tmpDir = "$env:TEMP\ltai-warp"
$null = New-Item -ItemType Directory -Path $tmpDir -Force
$msi = "$tmpDir\Cloudflare_WARP.msi"

$downloaded = $false
foreach ($url in $urls)
{
    Write-Host "📥 正在下载 Cloudflare WARP... ($url)" -ForegroundColor Cyan
    try
    {
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add("User-Agent", "LTAI-Accelerator/1.0")
        $wc.DownloadFile($url, $msi)
        if ((Get-Item $msi -ErrorAction SilentlyContinue).Length -ge 1MB)
        {
            $downloaded = $true
            Write-Host "   已保存: $msi" -ForegroundColor Gray
            break
        }
    }
    catch
    {
        Write-Host "   ❌ 失败: $_" -ForegroundColor Red
    }
}

if (-not $downloaded)
{
    Write-Host "❌ 所有下载源均失败" -ForegroundColor Red
    exit 1
}

# ── 提权检查 ────────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent())
    .IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin)
{
    Write-Host "⚠ 需要管理员权限才能静默安装。正在提权..." -ForegroundColor Yellow
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "powershell.exe"
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $psi.Verb = "runas"
    $p = [System.Diagnostics.Process]::Start($psi)
    if ($p)
    {
        Write-Host "   已在提权窗口中启动安装。原窗口可关闭。" -ForegroundColor Yellow
        exit 0
    }
    else
    {
        Write-Host "❌ 提权失败，请手动右键"以管理员身份运行"本脚本。" -ForegroundColor Red
        exit 1
    }
}

# ── 静默安装 ────────────────────────────────────────────────────
Write-Host "🔧 正在安装 (msiexec /quiet)..." -ForegroundColor Cyan
$proc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$msi`" /quiet /norestart" -Wait -NoNewWindow -PassThru

if ($proc.ExitCode -ne 0)
{
    Write-Host "❌ 安装失败 (msiexec exit=$($proc.ExitCode))" -ForegroundColor Red
    Write-Host "   尝试手动安装: $msi" -ForegroundColor Yellow
    exit 1
}

Write-Host "   msiexec 完成，等待 warp-cli 注册到 PATH..." -ForegroundColor Gray
Start-Sleep -Seconds 3

# ── 验证安装 ────────────────────────────────────────────────────
$installed = Get-Command "warp-cli" -ErrorAction SilentlyContinue
if (-not $installed)
{
    # warp-cli 可能在 Program Files 但 PATH 没刷新
    $candidates = @(
        "${env:ProgramFiles}\Cloudflare\Cloudflare WARP\warp-cli.exe",
        "${env:ProgramFiles(x86)}\Cloudflare\Cloudflare WARP\warp-cli.exe",
        "$env:LOCALAPPDATA\Cloudflare\Cloudflare WARP\warp-cli.exe"
    )
    foreach ($c in $candidates)
    {
        if (Test-Path $c)
        {
            $installed = $true
            Write-Host "   找到: $c" -ForegroundColor Gray
            break
        }
    }
}

if ($installed)
{
    Write-Host "✅ Cloudflare WARP 安装成功！" -ForegroundColor Green
    Write-Host "   启动 LTAI Accelerator 即可自动识别 Warp 代理。" -ForegroundColor Green
    try { & "warp-cli" --version } catch { }

    # 清理 MSI
    Remove-Item $msi -Force -ErrorAction SilentlyContinue
    exit 0
}
else
{
    Write-Host "⚠ 安装已完成但未能验证 warp-cli。请手动检查：" -ForegroundColor Yellow
    Write-Host "   安装文件: $msi" -ForegroundColor Yellow
    Write-Host "   重启终端或手动添加 PATH。" -ForegroundColor Yellow
    exit 0
}
