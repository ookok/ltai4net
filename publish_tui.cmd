@echo off
setlocal enabledelayedexpansion

echo ===== LTAI - Publish TUI =====

set DIST=%~dp0dist\TUI

echo [1/3] Clean dist
if exist "%DIST%" rmdir /s /q "%DIST%"
if errorlevel 1 exit /b 1
mkdir "%DIST%"
if errorlevel 1 exit /b 1

echo [2/3] Publish TUI
dotnet publish "src\LTAI.TUI\LTAI.TUI.csproj" -c Release -o "%DIST%" --nologo
if errorlevel 1 exit /b 1

echo [3/3] Copy runtime assets
if exist "%~dp0agents" xcopy /e /i /q "%~dp0agents" "%DIST%\agents" >nul
if errorlevel 1 exit /b 1
if exist "%~dp0skills" xcopy /e /i /q "%~dp0skills" "%DIST%\skills" >nul
if errorlevel 1 exit /b 1
if exist "%~dp0models" xcopy /e /i /q "%~dp0models" "%DIST%\models" >nul
if errorlevel 1 exit /b 1

echo.
echo Done!  dist/TUI/
