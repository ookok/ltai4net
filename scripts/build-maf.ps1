<#
.SYNOPSIS
  Pre-compile MAF (Microsoft Agent Framework) submodule to dist/lib/maf/ DLLs.
  Reduces LTAI incremental build time by eliminating 15 ProjectReference chains.

.DESCRIPTION
  Builds only the MAF projects that LTAI uses (see "MAF projects required" below)
  with net10.0 TFM, relaxed analyzer settings, and output to dist/lib/maf/.

  After running this script once, `dotnet build LTAI.sln` resolves MAF types
  from prebuilt DLLs instead of walking the full MAF dependency tree.

.NOTES
  Run from repo root:   .\scripts\build-maf.ps1
  Force rebuild:        .\scripts\build-maf.ps1 -Force
  Skip restore:         .\scripts\build-maf.ps1 -NoRestore
#>

param(
    [switch]$Force,
    [switch]$NoRestore
)

$RepoRoot = Split-Path -Parent $PSScriptRoot
$MafDir = Join-Path $RepoRoot "extern\agent-framework\dotnet"
$OutDir = Join-Path $RepoRoot "dist\lib\maf"

# ── MAF projects required by LTAI (15 leaf projects; transitive deps auto-pulled) ──
$Projects = @(
    # Referenced by LTAI.Core
    "src\Microsoft.Agents.AI\Microsoft.Agents.AI.csproj"

    # Referenced by LTAI.AI
    "src\Microsoft.Agents.AI.OpenAI\Microsoft.Agents.AI.OpenAI.csproj"
    "src\Microsoft.Agents.AI.Anthropic\Microsoft.Agents.AI.Anthropic.csproj"

    # Referenced by LTAI.Agent
    "src\Microsoft.Agents.AI.Tools.Shell\Microsoft.Agents.AI.Tools.Shell.csproj"
    "src\Microsoft.Agents.AI.Workflows\Microsoft.Agents.AI.Workflows.csproj"
    "src\Microsoft.Agents.AI.Workflows.Declarative\Microsoft.Agents.AI.Workflows.Declarative.csproj"
    "src\Microsoft.Agents.AI.Workflows.Declarative.Mcp\Microsoft.Agents.AI.Workflows.Declarative.Mcp.csproj"
    "src\Microsoft.Agents.AI.Harness\Microsoft.Agents.AI.Harness.csproj"
    "src\Microsoft.Agents.AI.Hosting\Microsoft.Agents.AI.Hosting.csproj"
    "src\Microsoft.Agents.AI.Mem0\Microsoft.Agents.AI.Mem0.csproj"
    "src\Microsoft.Agents.AI.Mcp\Microsoft.Agents.AI.Mcp.csproj"
    "src\Microsoft.Agents.AI.DurableTask\Microsoft.Agents.AI.DurableTask.csproj"

    # Referenced by LTAI.Web (DevUI kept as ProjectReference — Sdk.Web with frontend assets)
    "src\Microsoft.Agents.AI.Hosting.AspNetCore\Microsoft.Agents.AI.Hosting.AspNetCore.csproj"
    "src\Microsoft.Agents.AI.Hosting.A2A.AspNetCore\Microsoft.Agents.AI.Hosting.A2A.AspNetCore.csproj"
    "src\Microsoft.Agents.AI.Hosting.AGUI.AspNetCore\Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.csproj"
    "src\Microsoft.Agents.AI.Hosting.OpenAI\Microsoft.Agents.AI.Hosting.OpenAI.csproj"
)

# (build properties are passed inline in the command for MSBuild parsing compatibility)

# Ensure output directory exists
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " MAF Prebuild — output: $OutDir" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

$total = $Projects.Count
$ok = 0; $fail = 0

foreach ($rel in $Projects)
{
    $csproj = Join-Path $MafDir $rel
    $name = [System.IO.Path]::GetFileNameWithoutExtension($csproj)
    $dll = "$OutDir\$name.dll"

    if (!$Force -and (Test-Path $dll) -and ((Get-Item $dll).Length -gt 10KB))
    {
        Write-Host "  [skip] $name — already present" -ForegroundColor DarkGray
        $ok++
        continue
    }

    Write-Host "  [build] $name ..." -ForegroundColor Yellow
    # Use & dotnet with explicit arguments (no splatting or Invoke-Expression —
    # MSBuild property parsing is sensitive to quoting and spaces).
    $out = & dotnet build $csproj -f net10.0 -o $OutDir --no-restore `
        -p:TreatWarningsAsErrors=false `
        -p:RunAnalyzersDuringBuild=false `
        -p:GenerateDocumentationFile=false `
        -clp:NoSummary 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0)
    {
        Write-Host "         $name ✅" -ForegroundColor Green
        $ok++
    }
    else
    {
        Write-Host "         $name ❌ (exit=$exitCode)" -ForegroundColor Red
        $fail++
        # Show first few errors
        $out | Where-Object { $_ -match "error " } | Select-Object -First 5 | ForEach-Object {
            Write-Host "         $_" -ForegroundColor Red
        }
    }
}

Write-Host ""
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Done: $ok/$total succeeded, $fail failed" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan

# Clean up stale DLLs from previous builds (projects that were removed)
if ($ok -gt 0)
{
    $expected = $Projects | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension((Join-Path $MafDir $_)) }
    Get-ChildItem "$OutDir\*.dll" | Where-Object {
        $_.Name -notin ($expected | ForEach-Object { "$_.dll" }) -and
        $_.Name -ne "Microsoft.Agents.AI.Abstractions.dll" -and  # transitive dep
        $_.Name -like "Microsoft.Agents.AI*"
    } | ForEach-Object {
        Write-Host "  [cleanup] $($_.Name) — stale" -ForegroundColor DarkGray
        Remove-Item $_.FullName
    }
}
