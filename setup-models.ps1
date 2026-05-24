<#
.SYNOPSIS
    LTAI 一键模型下载脚本
.DESCRIPTION
    自动检测硬件，智能选择最佳模型组合，一键下载所有需要的模型文件。
    支持 HuggingFace 直连和 hf-mirror.com 国内镜像自动切换。
.PARAMETER Layers
    要下载的模型层级: L0, L1, L2。默认 auto（根据硬件自动选择）。
.PARAMETER Mirror
    强制使用 hf-mirror.com 国内镜像下载。
.PARAMETER Type
    下载类型: all（全部）, recommended（仅推荐）, minimal（最小集）。
    默认 recommended。
.PARAMETER Force
    强制重新下载已存在的模型。
.PARAMETER OutputDir
    模型输出目录，默认自动查找项目下的 models 目录。
.PARAMETER DryRun
    仅预览将要下载的模型列表，不实际下载。
.EXAMPLE
    .\setup-models.ps1
    自动检测硬件，下载推荐模型组合。
.EXAMPLE
    .\setup-models.ps1 -Layers L1 -Type all
    下载所有 L1 模型。
.EXAMPLE
    .\setup-models.ps1 -Mirror -Type minimal
    使用国内镜像下载最小模型集。
.EXAMPLE
    .\setup-models.ps1 -Layers L0,L1,L2 -Type all -Mirror -Force
    使用国内镜像下载全部模型，覆盖已存在的。
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', 'L0', 'L1', 'L2')]
    [string[]]$Layers = @('auto'),

    [switch]$Mirror,

    [ValidateSet('all', 'recommended', 'minimal')]
    [string]$Type = 'recommended',

    [switch]$Force,

    [string]$OutputDir,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ============================================================
# 模型目录
# ============================================================
$hostOS = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'macos' } else { 'windows' }

# ============================================================
# 颜色输出
# ============================================================
function Write-Color($Text, $Color = 'White') {
    $prev = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = $Color
    Write-Host $Text
    $host.UI.RawUI.ForegroundColor = $prev
}

function Write-Success($Text) { Write-Host "   ✓ $Text" -ForegroundColor Green }
function Write-Error2($Text) { Write-Host "   ✗ $Text" -ForegroundColor Red }
function Write-Warn($Text) { Write-Host "   ⚠ $Text" -ForegroundColor Yellow }
function Write-Info($Text) { Write-Host "   📌 $Text" -ForegroundColor Cyan }

# ============================================================
# 硬件检测
# ============================================================
function Get-HardwareInfo {
    $cpuCores = [Environment]::ProcessorCount

    $memoryMB = 0
    try {
        if ($IsWindows) {
            $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
            if ($cs) { $memoryMB = [math]::Round($cs.TotalPhysicalMemory / 1MB) }
        } elseif ($IsLinux) {
            $meminfo = Get-Content /proc/meminfo 2>$null
            $memTotalLine = $meminfo | Where-Object { $_ -match 'MemTotal:' }
            if ($memTotalLine -and $memTotalLine -match '(\d+)') {
                $memoryMB = [math]::Round([int]$Matches[1] / 1024)
            }
        } elseif ($IsMacOS) {
            $mem = sysctl -n hw.memsize 2>$null
            if ($mem) { $memoryMB = [math]::Round([long]$mem / 1MB) }
        }
    } catch {}
    if ($memoryMB -le 0) { $memoryMB = 4096 }

    $hasGPU = $false; $gpuName = 'None'
    try {
        if ($IsWindows) {
            $gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'OK' -and $_.Name -notmatch 'Microsoft' }
            if ($gpu) { $hasGPU = $true; $gpuName = ($gpu | Select-Object -First 1).Name }
        }
    } catch {}

    $recommendedEngine = if ($hasGPU -and $memoryMB -ge 16384) { 'gguf' } else { 'gguf' }

    return @{
        CpuCores = $cpuCores
        MemoryMB = $memoryMB
        HasGPU = $hasGPU
        GpuName = $gpuName
        RecommendedEngine = $recommendedEngine
    }
}

# ============================================================
# 模型目录定义
# ============================================================
$script:AllModels = @(
    # ========== L0: Embedding + TTS Models (ONNX) ==========
    @{ Version = 'bge-large-zh-v1.5-onnx'; Name = 'BGE-Large-ZH-v1.5 (ONNX) - 中文嵌入'; Layer = 'L0'; Engine = 'onnx'; SizeMB = 1200; RAM_MB = 2048; FileName = 'model.onnx'; Url = 'https://huggingface.co/BAAI/bge-large-zh-v1.5/resolve/main/onnx/model.onnx'; MirrorUrl = 'https://hf-mirror.com/BAAI/bge-large-zh-v1.5/resolve/main/onnx/model.onnx'; Tier = 'standard' },
    @{ Version = 'bge-small-zh-v1.5-onnx'; Name = 'BGE-Small-ZH-v1.5 (ONNX) - 轻量中文嵌入'; Layer = 'L0'; Engine = 'onnx'; SizeMB = 95; RAM_MB = 1024; FileName = 'model.onnx'; Url = 'https://huggingface.co/Xenova/bge-small-zh-v1.5/resolve/main/onnx/model.onnx'; MirrorUrl = 'https://hf-mirror.com/Xenova/bge-small-zh-v1.5/resolve/main/onnx/model.onnx'; Tier = 'minimal' },
    @{ Version = 'bge-m3-onnx'; Name = 'BGE-M3 (ONNX) - 多语言嵌入'; Layer = 'L0'; Engine = 'onnx'; SizeMB = 2200; RAM_MB = 4096; FileName = 'model.onnx'; Url = 'https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx'; MirrorUrl = 'https://hf-mirror.com/BAAI/bge-m3/resolve/main/onnx/model.onnx'; Tier = 'premium' },
    @{ Version = 'supertonic-3-onnx'; Name = 'Supertonic 3 (ONNX) - 31语言 TTS'; Layer = 'L0'; Engine = 'onnx-lfs'; SizeMB = 400; RAM_MB = 2048; FileName = 'assets/onnx/duration_predictor.onnx'; Url = 'https://huggingface.co/Supertone/supertonic-3'; MirrorUrl = 'https://hf-mirror.com/Supertone/supertonic-3'; Tier = 'standard'; Comment = 'Git LFS clone. Use: git lfs install && git clone https://huggingface.co/Supertone/supertonic-3 assets' },

    # ========== L1: Fast Models (GGUF) ==========
    @{ Version = 'rwkv7-g1-0.4b-q4'; Name = 'RWKV-7 G1 0.4B (Q4_K_M) - 极轻量'; Layer = 'L1'; Engine = 'gguf'; SizeMB = 250; RAM_MB = 2048; FileName = 'rwkv7-g1-0.4b-q4.gguf'; Url = 'https://huggingface.co/Mungert/rwkv7-0.4B-g1-GGUF/resolve/main/rwkv7-0.4b-g1-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/Mungert/rwkv7-0.4B-g1-GGUF/resolve/main/rwkv7-0.4b-g1-q4_k_m.gguf'; Tier = 'minimal' },
    @{ Version = 'rwkv7-g1-1.5b-q4'; Name = 'RWKV-7 G1 1.5B (Q4_K_M) - 中文优化'; Layer = 'L1'; Engine = 'gguf'; SizeMB = 990; RAM_MB = 4096; FileName = 'rwkv7-g1-1.5b-q4.gguf'; Url = 'https://huggingface.co/zhiyuan8/RWKV-v7-1.5B-G1-GGUF/resolve/main/rwkv7-1.5b-g1-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/zhiyuan8/RWKV-v7-1.5B-G1-GGUF/resolve/main/rwkv7-1.5b-g1-q4_k_m.gguf'; Tier = 'standard' },
    @{ Version = 'rwkv7-g1-2.9b-q4'; Name = 'RWKV-7 G1 2.9B (Q4_K_M) - 中文增强'; Layer = 'L1'; Engine = 'gguf'; SizeMB = 1880; RAM_MB = 8192; FileName = 'rwkv7-g1-2.9b-q4.gguf'; Url = 'https://huggingface.co/zhiyuan8/RWKV-v7-2.9B-G1-GGUF/resolve/main/rwkv7-2.9b-g1-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/zhiyuan8/RWKV-v7-2.9B-G1-GGUF/resolve/main/rwkv7-2.9b-g1-q4_k_m.gguf'; Tier = 'standard' },
    @{ Version = 'qwen2.5-1.5b-q4'; Name = 'Qwen2.5 1.5B (Q4_K_M) - 中文最强'; Layer = 'L1'; Engine = 'gguf'; SizeMB = 1100; RAM_MB = 4096; FileName = 'qwen2.5-1.5b-q4.gguf'; Url = 'https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf'; Tier = 'standard' },
    @{ Version = 'qwen2.5-3b-q4'; Name = 'Qwen2.5 3B (Q4_K_M) - 中文旗舰'; Layer = 'L1'; Engine = 'gguf'; SizeMB = 2000; RAM_MB = 8192; FileName = 'qwen2.5-3b-q4.gguf'; Url = 'https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf'; Tier = 'premium' },

    # ========== L2: Deep Models (GGUF) ==========
    @{ Version = 'qwen2.5-7b-q4'; Name = 'Qwen2.5 7B (Q4_K_M) - 深度推理'; Layer = 'L2'; Engine = 'gguf'; SizeMB = 4400; RAM_MB = 16384; FileName = 'qwen2.5-7b-q4.gguf'; Url = 'https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/qwen2.5-7b-instruct-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/qwen2.5-7b-instruct-q4_k_m.gguf'; Tier = 'standard' },
    @{ Version = 'qwen2.5-14b-q4'; Name = 'Qwen2.5 14B (Q4_K_M) - 旗舰深度'; Layer = 'L2'; Engine = 'gguf'; SizeMB = 9000; RAM_MB = 32768; FileName = 'qwen2.5-14b-q4.gguf'; Url = 'https://huggingface.co/Qwen/Qwen2.5-14B-Instruct-GGUF/resolve/main/qwen2.5-14b-instruct-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/Qwen/Qwen2.5-14B-Instruct-GGUF/resolve/main/qwen2.5-14b-instruct-q4_k_m.gguf'; Tier = 'premium' },
    @{ Version = 'deepseek-r1-distill-qwen-7b-q4'; Name = 'DeepSeek-R1-Distill-Qwen-7B (Q4_K_M) - 推理增强'; Layer = 'L2'; Engine = 'gguf'; SizeMB = 4700; RAM_MB = 16384; FileName = 'deepseek-r1-distill-qwen-7b-q4.gguf'; Url = 'https://huggingface.co/unsloth/DeepSeek-R1-Distill-Qwen-7B-GGUF/resolve/main/deepseek-r1-distill-qwen-7b-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/unsloth/DeepSeek-R1-Distill-Qwen-7B-GGUF/resolve/main/deepseek-r1-distill-qwen-7b-q4_k_m.gguf'; Tier = 'standard' },
    @{ Version = 'deepseek-r1-distill-llama-8b-q4'; Name = 'DeepSeek-R1-Distill-Llama-8B (Q4_K_M) - 推理增强'; Layer = 'L2'; Engine = 'gguf'; SizeMB = 4900; RAM_MB = 16384; FileName = 'deepseek-r1-distill-llama-8b-q4.gguf'; Url = 'https://huggingface.co/unsloth/DeepSeek-R1-Distill-Llama-8B-GGUF/resolve/main/deepseek-r1-distill-llama-8b-q4_k_m.gguf'; MirrorUrl = 'https://hf-mirror.com/unsloth/DeepSeek-R1-Distill-Llama-8B-GGUF/resolve/main/deepseek-r1-distill-llama-8b-q4_k_m.gguf'; Tier = 'standard' }
)

# ============================================================
# 模型选择逻辑
# ============================================================
function Select-Models {
    param($Hardware, $TargetLayers, $DownloadType)

    $selected = @()
    $targetLayerSet = [System.Collections.Generic.HashSet[string]]::new()
    $TargetLayers | ForEach-Object { [void]$targetLayerSet.Add($_.ToUpper()) }

    foreach ($m in $script:AllModels) {
        if ($targetLayerSet.Count -gt 0 -and -not $targetLayerSet.Contains($m.Layer)) { continue }
        if ($m.RAM_MB -gt ($Hardware.MemoryMB * 0.7)) { continue }

        if ($DownloadType -eq 'all') {
            $selected += $m
        }
        elseif ($DownloadType -eq 'minimal') {
            if ($m.Tier -eq 'minimal') { $selected += $m }
            # minimum L0 + minimum L1 per layer
            if ($m.Layer -eq 'L0' -and $m.Tier -eq 'minimal') { $selected += $m }
        }
        else {
            # recommended: best per layer based on RAM
            # L0: bge-large-zh (standard) if >= 2GB, else bge-small (minimal)
            # L1: Qwen2.5 1.5B if 4GB+, Qwen2.5 3B if 8GB+, else RWKV 0.4B
            # L2: Qwen2.5 7B if 16GB+, else skip
            if ($m.Layer -eq 'L0') {
                if ($Hardware.MemoryMB -ge 2048 -and $m.Version -eq 'bge-large-zh-v1.5-onnx') { $selected += $m }
                elseif ($Hardware.MemoryMB -lt 2048 -and $m.Version -eq 'bge-small-zh-v1.5-onnx') { $selected += $m }
            }
            elseif ($m.Layer -eq 'L1') {
                if ($Hardware.MemoryMB -ge 8192 -and $m.Version -eq 'qwen2.5-3b-q4') { $selected += $m }
                elseif ($Hardware.MemoryMB -ge 4096 -and $m.Version -eq 'qwen2.5-1.5b-q4') { $selected += $m }
                elseif ($Hardware.MemoryMB -ge 2048 -and $m.Version -eq 'rwkv7-g1-0.4b-q4') { $selected += $m }
            }
            elseif ($m.Layer -eq 'L2') {
                if ($Hardware.MemoryMB -ge 16384 -and $m.Version -eq 'qwen2.5-7b-q4') { $selected += $m }
            }
        }
    }

    return $selected | Sort-Object Layer, SizeMB
}

# ============================================================
# 模型目录解析
# ============================================================
function Get-ModelsDir {
    param($SpecifiedDir)
    if ($SpecifiedDir) { return $SpecifiedDir }

    $scriptDir = Split-Path -Parent $PSScriptRoot
    $modelsDir = Join-Path $scriptDir 'models'
    if (Test-Path $modelsDir) { return $modelsDir }
    return (Join-Path $PSScriptRoot 'models')
}

function Get-ModelFilePath {
    param($Model, $BaseDir)
    $layerDir = Join-Path $BaseDir $Model.Layer.ToLower()
    if ($Model.Layer -eq 'L0' -and $Model.Engine -eq 'onnx') {
        return Join-Path $layerDir 'model.onnx'
    }
    return Join-Path $layerDir $Model.FileName
}

# ============================================================
# 下载单个模型
# ============================================================
function Invoke-ModelDownload {
    param($Model, $BaseDir, [switch]$UseMirror)

    $urls = if ($UseMirror) { @($Model.MirrorUrl, $Model.Url) } else { @($Model.Url, $Model.MirrorUrl) }
    $filePath = Get-ModelFilePath -Model $Model -BaseDir $BaseDir
    $dir = Split-Path -Parent $filePath
    
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    $lastError = $null
    foreach ($url in $urls) {
        if (-not $url) { continue }
        try {
            Write-Info "尝试下载: $([System.Uri]::new($url).Host)"
            $startTime = Get-Date

            # Windows: use BITS if available for large files
            if ($IsWindows -and $Model.SizeMB -gt 500) {
                try {
                    $jobName = "LTAI_Model_$(Get-Random)"
                    Start-BitsTransfer -Source $url -Destination $filePath -DisplayName $jobName -ErrorAction Stop
                    $endTime = Get-Date
                    $elapsed = ($endTime - $startTime).TotalSeconds
                    $speed = if ($elapsed -gt 0) { [math]::Round($Model.SizeMB / $elapsed, 1) } else { 0 }
                    Write-Success "下载完成 ($speed MB/s)"
                    return $true
                } catch {
                    Write-Warn "BITS 下载失败，切换到 Invoke-WebRequest..."
                    Remove-Item -Path $filePath -ErrorAction SilentlyContinue
                }
            }

            # Fallback: Invoke-WebRequest
            Invoke-WebRequest -Uri $url -OutFile $filePath -ErrorAction Stop
            $endTime = Get-Date
            $elapsed = ($endTime - $startTime).TotalSeconds
            $speed = if ($elapsed -gt 0) { [math]::Round($Model.SizeMB / $elapsed, 1) } else { 0 }
            Write-Success "下载完成 ($speed MB/s)"
            return $true
        } catch {
            $lastError = $_
            Write-Warn "从 $($url) 下载失败: $($_.Exception.Message)"
            Remove-Item -Path $filePath -ErrorAction SilentlyContinue
        }
    }

    Write-Error2 "所有下载源失败"
    return $false
}

# ============================================================
# 主流程
# ============================================================
function Main {
    Write-Color '┌─────────────────────────────────────────────────────┐' 'Cyan'
    Write-Color '│           LTAI 模型一键部署脚本                      │' 'Cyan'
    Write-Color '└─────────────────────────────────────────────────────┘' 'Cyan'
    Write-Host ''

    # 硬件检测
    $hw = Get-HardwareInfo
    Write-Info "硬件检测: $($hw.CpuCores) 核 | $($hw.MemoryMB)MB 内存 | GPU: $($hw.GpuName)"
    Write-Info "推荐引擎: $($hw.RecommendedEngine.ToUpper())"
    Write-Host ''

    # 解析 Layers
    $targetLayers = if ($Layers -contains 'auto') { @('L0', 'L1', 'L2') } else { $Layers }
    Write-Info "目标层级: $($targetLayers -join ', ')"
    Write-Info "下载类型: $Type"
    if ($Mirror) { Write-Info "镜像模式: hf-mirror.com" }
    Write-Host ''

    # 模型目录
    $modelsDir = Get-ModelsDir($OutputDir)
    Write-Info "模型目录: $modelsDir"
    New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
    Write-Host ''

    # 选择模型
    $selected = Select-Models -Hardware $hw -TargetLayers $targetLayers -DownloadType $Type
    
    if ($selected.Count -eq 0) {
        Write-Warn "没有找到符合条件的模型。"
        Write-Warn "内存: $($hw.MemoryMB)MB, 层级: $($targetLayers -join ', '), 类型: $Type"
        return
    }

    # 显示模型列表
    $totalSize = ($selected | Measure-Object -Property SizeMB -Sum).Sum
    Write-Color '=== 模型下载计划 ===' 'Cyan'
    Write-Host ''
    Write-Host ('| {0,-3} | {1,-45} | {2,-8} | {3,-10} |' -f '#', 'Model', 'Size', 'RAM Need')
    Write-Host ('|{0}-|{1}-|{2}-|{3}-|' -f '---', '----------------------------------------', '------', '-----')
    for ($i = 0; $i -lt $selected.Count; $i++) {
        $m = $selected[$i]
        $ram = if ($m.RAM_MB -ge 1024) { "$([math]::Round($m.RAM_MB/1024,1)) GB" } else { "$($m.RAM_MB) MB" }
        Write-Host ('| {0,3} | {1,-45} | {2,6} MB | {3,-10} |' -f ($i + 1), $m.Name, $m.SizeMB, $ram)
    }
    Write-Host ''
    Write-Info "合计: $totalSize MB ($([math]::Round($totalSize/1024,2)) GB)"
    Write-Host ''

    # 检查已安装状态
    $toDownload = @()
    $alreadyInstalled = @()
    foreach ($m in $selected) {
        $fp = Get-ModelFilePath -Model $m -BaseDir $modelsDir
        if (Test-Path $fp -and -not $Force) {
            $size = (Get-Item $fp).Length
            $alreadyInstalled += @{ Model = $m; Path = $fp; Size = [math]::Round($size / 1MB, 1) }
        } else {
            $toDownload += $m
        }
    }

    if ($alreadyInstalled.Count -gt 0) {
        Write-Color "已安装 ($($alreadyInstalled.Count) 个):" 'Green'
        foreach ($x in $alreadyInstalled) {
            Write-Host "   ✓ $($x.Model.Name) ($($x.Size) MB)"
        }
        Write-Host ''
    }

    if ($DryRun) {
        Write-Info "DryRun 模式，不执行实际下载。"
        Write-Host ''
        Write-Color "计划下载 $($toDownload.Count) 个模型, 共 $((($toDownload | Measure-Object -Property SizeMB -Sum).Sum)) MB - 已跳过" 'Yellow'
        return
    }

    if ($toDownload.Count -eq 0) {
        Write-Success '所有模型已安装完毕！'
        Write-Host ''
        Write-Color '运行 ltai setup 可重新配置模型选择。' 'Gray'
        return
    }

    $dlTotalMB = ($toDownload | Measure-Object -Property SizeMB -Sum).Sum
    Write-Color "准备下载 $($toDownload.Count) 个模型, 共 $dlTotalMB MB ($([math]::Round($dlTotalMB/1024,2)) GB)" 'Yellow'
    Write-Host ''

    $confirm = if ($dlTotalMB -gt 5000) {
        $host.UI.PromptForChoice('确认下载', "将下载 $dlTotalMB MB 模型数据，继续？", @('&Yes', '&No'), 0)
    } else { 0 }

    if ($confirm -ne 0) {
        Write-Warn '用户取消下载。'
        return
    }
    Write-Host ''

    # 执行下载
    $successCount = 0
    $failCount = 0
    $totalStart = Get-Date

    for ($i = 0; $i -lt $toDownload.Count; $i++) {
        $m = $toDownload[$i]
        $pct = [math]::Round(($i + 1) / $toDownload.Count * 100, 0)
        Write-Color ('[{0}/{1}] {2}% {3} ({4})' -f ($i + 1), $toDownload.Count, $pct, $m.Name, $m.Layer) 'Cyan'
        Write-Info "大小: $($m.SizeMB) MB | 引擎: $($m.Engine.ToUpper())"

        if (Invoke-ModelDownload -Model $m -BaseDir $modelsDir -UseMirror:$Mirror) {
            $successCount++
        } else {
            $failCount++
        }
        Write-Host ''
    }

    $totalEnd = Get-Date
    $totalElapsed = [math]::Round(($totalEnd - $totalStart).TotalMinutes, 1)

    # 总结
    Write-Color '========================================================' 'Cyan'
    Write-Color '  下载完成!' 'Green'
    Write-Host ''
    Write-Host "  成功: $successCount | 失败: $failCount"
    Write-Host "  耗时: $totalElapsed 分钟"
    Write-Host "  模型目录: $modelsDir"
    Write-Host ''

    # 显示安装的模型
    Write-Color '  已安装模型:' 'Cyan'
    $layers = @('l0', 'l1', 'l2')
    foreach ($l in $layers) {
        $ld = Join-Path $modelsDir $l
        if (Test-Path $ld) {
            $files = Get-ChildItem $ld -File
            if ($files.Count -gt 0) {
                Write-Color "  [$($l.ToUpper())]" 'Yellow'
                foreach ($f in $files) {
                    $sz = [math]::Round($f.Length / 1MB, 1)
                    Write-Host "    $($f.Name) ($sz MB)"
                }
            }
        }
    }

    Write-Host ''
    Write-Color '  下一步:' 'Cyan'
    Write-Host '    1. 运行 ltai setup 配置模型选择'
    Write-Host '    2. 运行 ltai model list 查看所有模型状态'
    Write-Host '    3. 运行 ltai model reset 清除所有模型并重新配置'
}

Main
