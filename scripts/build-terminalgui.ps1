<#
.SYNOPSIS
  Pre-compile Terminal.Gui submodule to dist/lib/terminal.gui/ DLL.
  Reduces LTAI incremental build time by using a prebuilt DLL reference.

.DESCRIPTION
  Builds Terminal.Gui from extern/Terminal.Gui with net10.0 TFM and
  copies the output DLL + XML docs to dist/lib/terminal.gui/.

  After running this script once, `dotnet build LTAI.sln` resolves
  Terminal.Gui types from the prebuilt DLL instead of building from source.

.NOTES
  Run from repo root:   .\scripts\build-terminalgui.ps1
  Force rebuild:        .\scripts\build-terminalgui.ps1 -Force
#>

param(
    [switch]$Force
)

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SrcDir = Join-Path $RepoRoot "extern\Terminal.Gui\Terminal.Gui"
$OutDir = Join-Path $RepoRoot "dist\lib\terminal.gui"

# ── Skip if DLL already exists and not forced ──
$TargetDll = Join-Path $OutDir "Terminal.Gui.dll"
if (-not $Force -and (Test-Path $TargetDll)) {
    Write-Host "  [skip] Terminal.Gui — already present (use -Force to rebuild)"
    exit 0
}

Write-Host "══════════════════════════════════════════════════"
Write-Host " Terminal.Gui Prebuild — output: $OutDir"
Write-Host "══════════════════════════════════════════════════"

# Ensure output directory exists
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# Build Terminal.Gui
Write-Host "  Building Terminal.Gui..."
$result = & dotnet build $SrcDir\Terminal.Gui.csproj --nologo -c Release /p:Version=2.4.5 /p:GitVersion_NoOutputEnabled=true 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAILED to build Terminal.Gui"
    Write-Host $result
    exit 1
}

Write-Host "  Copying DLL to $OutDir..."
Copy-Item "$SrcDir\bin\Release\net10.0\Terminal.Gui.dll" $OutDir -Force
Copy-Item "$SrcDir\bin\Release\net10.0\Terminal.Gui.xml" $OutDir -Force -ErrorAction SilentlyContinue

Write-Host "══════════════════════════════════════════════════"
Write-Host " Done: Terminal.Gui prebuilt"
Write-Host "══════════════════════════════════════════════════"
