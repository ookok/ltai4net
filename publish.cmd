@echo off
setlocal enabledelayedexpansion

echo ===== LTAI - Publish All =====

set DIST=%~dp0dist

echo [1/4] Clean dist
if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"

echo [2/4] Publish 4 entry points
dotnet publish "src\LTAI.Cli\LTAI.Cli.csproj"     -c Release -o "%DIST%\CLI"     --nologo
dotnet publish "src\LTAI.TUI\LTAI.TUI.csproj"    -c Release -o "%DIST%\TUI"     --nologo
dotnet publish "src\LTAI.Desktop\LTAI.Desktop.csproj" -c Release -o "%DIST%\Desktop" --nologo
dotnet publish "src\LTAI.Web\LTAI.Web.csproj"    -c Release -o "%DIST%\Web"     --nologo

echo [3/4] Copy runtime assets (agents, skills, models)
for %%D in (CLI TUI Desktop Web) do (
    if exist "%DIST%\%%D" (
        xcopy /e /i /q "agents"  "%DIST%\%%D\agents"  >nul
        xcopy /e /i /q "skills"  "%DIST%\%%D\skills"  >nul
        xcopy /e /i /q "models"  "%DIST%\%%D\models"  >nul
    )
)

echo [4/4] Clean intermediate output
if exist "%DIST%\lib" rmdir /s /q "%DIST%\lib"

echo.
echo Done!
echo   dist/CLI/     - dotnet CLI
echo   dist/TUI/     - Terminal UI
echo   dist/Desktop/ - Avalonia Desktop
echo   dist/Web/     - Web API
