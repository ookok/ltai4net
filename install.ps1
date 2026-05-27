# LTAI Agent OS — Windows PowerShell Installer
# Usage: iwr https://raw.githubusercontent.com/ookok/ltai4net/main/install.ps1 -OutFile install.ps1; ./install.ps1

param(
    [string]$Version = "latest",
    [string]$InstallDir = "$env:USERPROFILE\.ltai"
)

$ErrorActionPreference = "Stop"
$Repo = "ookok/ltai4net"

function Write-Info  { Write-Host "→ $args" -ForegroundColor Cyan }
function Write-Ok    { Write-Host "✓ $args" -ForegroundColor Green }
function Write-Err   { Write-Host "✗ $args" -ForegroundColor Red; exit 1 }

# detect arch
$arch = if ([Environment]::Is64BitOperatingSystem) { "x64" } else { "x86" }
$platform = "windows-${arch}"
$filename = "ltai-${platform}.exe"

Write-Host ""
Write-Host "  LTAI Agent OS — CLI Installer" -ForegroundColor Cyan
Write-Host "  V1.0  |  ${Repo}"
Write-Host ""

Write-Info "Platform: ${platform}"
Write-Info "Install:  ${InstallDir}"

# download
$url = if ($Version -eq "latest") {
    "https://github.com/${Repo}/releases/latest/download/${filename}"
} else {
    "https://github.com/${Repo}/releases/download/${Version}/${filename}"
}

Write-Info "Downloading ${filename}..."
Write-Info "  ${url}"

$binDir = "${InstallDir}\bin"
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$outPath = "${binDir}\ltai.exe"
Invoke-WebRequest -Uri $url -OutFile $outPath -UseBasicParsing

Write-Ok "Downloaded to ${outPath}"

# add to PATH
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*${binDir}*") {
    [Environment]::SetEnvironmentVariable("Path", "${userPath};${binDir}", "User")
    $env:Path = "${env:Path};${binDir}"
    Write-Ok "Added to PATH: ${binDir}"
}

Write-Host ""
Write-Ok "LTAI CLI installed!"
Write-Host ""
Write-Host "  Next steps:"
Write-Host "    ltai init          Configure your environment"
Write-Host "    ltai install       Download core runtime"
Write-Host "    ltai up            Start TUI"
Write-Host ""

$answer = Read-Host "  Run 'ltai init' now? [Y/n]"
if ($answer -ne "n" -and $answer -ne "N") {
    & "${outPath}" init
}
