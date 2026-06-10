<#
.SYNOPSIS
  Pre-compile Terminal.Gui.Editor submodule to dist/lib/editor/ DLL.

.DESCRIPTION
  Builds gui-cs/Editor from extern/Editor with net10.0 TFM and
  copies the output DLL + XML docs to dist/lib/editor/.

  Run from repo root:   .\scripts\build-editor.ps1
  Force rebuild:        .\scripts\build-editor.ps1 -Force
#>

param([switch]$Force)

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SrcDir = Join-Path $RepoRoot "extern\Editor\src\Terminal.Gui.Editor"
$OutDir = Join-Path $RepoRoot "dist\lib\editor"
$TargetDll = Join-Path $OutDir "Terminal.Gui.Editor.dll"

if (-not $Force -and (Test-Path $TargetDll)) {
    Write-Host "  [skip] Terminal.Gui.Editor — already present (use -Force to rebuild)"
    exit 0
}

Write-Host "══════════════════════════════════════════════════"
Write-Host " Editor Prebuild — output: $OutDir"
Write-Host "══════════════════════════════════════════════════"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Write-Host "  Building Terminal.Gui.Editor..."
$result = & dotnet build $SrcDir\Terminal.Gui.Editor.csproj --nologo -c Release /p:Version=1.0.0 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAILED"
    Write-Host $result
    exit 1
}

Write-Host "  Copying DLL..."
Copy-Item "$SrcDir\bin\Release\net10.0\Terminal.Gui.Editor.dll" $OutDir -Force
Copy-Item "$SrcDir\bin\Release\net10.0\Terminal.Gui.Editor.xml" $OutDir -Force -ErrorAction SilentlyContinue

Write-Host "══════════════════════════════════════════════════"
Write-Host " Done: Editor prebuilt"
Write-Host "══════════════════════════════════════════════════"
