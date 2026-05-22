# Publish all LTAI executables as single-file self-contained binaries
# Output: dist/publish/{Host|TUI|MCP|Cli|Desktop|WebApp}/LTAI.{name}.exe
# Each exe includes the .NET 10 runtime — no SDK installation needed on target machine.

param(
    [string]$Runtime = "win-x64",
    [switch]$Trimmed = $false
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$exeProjects = @(
    @{Name="Host";   Project="src/LTAI.Host/LTAI.Host.csproj"},
    @{Name="TUI";    Project="src/LTAI.TUI/LTAI.TUI.csproj"},
    @{Name="MCP";    Project="src/LTAI.MCP/LTAI.MCP.csproj"},
    @{Name="Cli";    Project="src/LTAI.Cli/LTAI.Cli.csproj"},
    @{Name="Desktop";Project="src/LTAI.Desktop/LTAI.Desktop.csproj"},
    @{Name="WebApp"; Project="src/LTAI.WebApp/LTAI.WebApp.csproj"}
)

$trimFlag = if ($Trimmed) { "-p:PublishTrimmed=true" } else { "" }

foreach ($proj in $exeProjects) {
    Write-Host "Publishing LTAI.$($proj.Name)..." -ForegroundColor Cyan
    dotnet publish "$root/$($proj.Project)" `
        -r $Runtime `
        -c Release `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        $trimFlag `
        -o "$root/dist/publish/$($proj.Name)" `
        2>&1 | Select-Object -Last 3

    $exeName = if ($Runtime -like "*win*") { "LTAI.$($proj.Name).exe" } else { "LTAI.$($proj.Name)" }
    $exePath = "$root/dist/publish/$($proj.Name)/$exeName"
    if (Test-Path $exePath) {
        $size = (Get-Item $exePath).Length / 1MB
        Write-Host "  OK: $exeName ({0:F0} MB)" -f $size -ForegroundColor Green
    } else {
        Write-Host "  FAIL: $exeName not found" -ForegroundColor Red
    }
}

Write-Host "`nDone. Output: $root/dist/publish/" -ForegroundColor Yellow
Write-Host "  Each folder contains a SINGLE self-contained executable."
Write-Host "  Copy the exe to any Windows machine — no .NET installation needed."
