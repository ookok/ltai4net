# LTAI CLI — Windows 一键安装脚本
# 用法: powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = "Stop"
$LTAI_DIR = "$env:USERPROFILE\.ltai"

Write-Host "=== LTAI 安装脚本 ===" -ForegroundColor Green
Write-Host ""

# 1. 下载 CLI
$CLI_URL = "https://github.com/ltai-org/ltai4net/releases/latest/download/ltai.exe"
$CLI_PATH = "$LTAI_DIR\ltai.exe"
if (!(Test-Path $LTAI_DIR)) { New-Item -ItemType Directory -Path $LTAI_DIR -Force | Out-Null }

    Write-Host "[1/3] 下载 CLI..." -ForegroundColor Yellow
    try {
        Invoke-WebRequest -Uri $CLI_URL -OutFile $CLI_PATH -ErrorAction Stop
        # Verify integrity: download checksum file and validate
        try {
            $hashUrl = "$CLI_URL.sha256"
            $expectedHash = (Invoke-WebRequest -Uri $hashUrl -ErrorAction SilentlyContinue).Content.Trim()
            if ($expectedHash) {
                $actualHash = (Get-FileHash -Path $CLI_PATH -Algorithm SHA256).Hash.ToLower()
                if ($actualHash -eq $expectedHash) {
                    Write-Host "  ✅ SHA256 verified" -ForegroundColor Green
                } else {
                    Write-Host "  ⚠️ SHA256 mismatch! Expected: $expectedHash, Got: $actualHash" -ForegroundColor Red
                }
            }
        } catch { }
        Write-Host "  ✅ 已下载: $CLI_PATH" -ForegroundColor Green
} catch {
    Write-Host "  ⚠️ 下载失败，请手动下载到 $CLI_PATH" -ForegroundColor Red
    Write-Host "     下载地址: $CLI_URL" -ForegroundColor Gray
}

# 2. 添加到 PATH
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$LTAI_DIR*") {
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$LTAI_DIR", "User")
    Write-Host "[2/3] 已添加到 PATH" -ForegroundColor Green
    $env:Path += ";$LTAI_DIR"
} else {
    Write-Host "[2/3] PATH 已包含 LTAI" -ForegroundColor Gray
}

# 3. 下载 ONNX 模型（可选）
$MODEL_DIR = "$LTAI_DIR\models\minilm-l6-v2"
if (!(Test-Path $MODEL_DIR)) {
    Write-Host "[3/3] 下载嵌入模型 (90MB, 可选)..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $MODEL_DIR -Force | Out-Null
    try {
        Invoke-WebRequest -Uri "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx" -OutFile "$MODEL_DIR\model.onnx"
        Invoke-WebRequest -Uri "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt" -OutFile "$MODEL_DIR\vocab.txt"
        Write-Host "  ✅ 模型已下载" -ForegroundColor Green
    } catch {
        Write-Host "  ⚠️ 模型下载失败，可稍后手动下载" -ForegroundColor Red
    }
} else {
    Write-Host "[3/3] 模型已存在，跳过" -ForegroundColor Gray
}

Write-Host ""
Write-Host "=== 安装完成 ===" -ForegroundColor Green
Write-Host "运行 ltai 开始使用" -ForegroundColor Cyan
