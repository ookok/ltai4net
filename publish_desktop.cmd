@echo off
setlocal enabledelayedexpansion

echo ===== LTAI - Publish Desktop =====

set DIST=%~dp0dist\Desktop

echo [1/3] Clean dist
if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"

echo [2/3] Publish Desktop
dotnet publish "src\LTAI.Desktop\LTAI.Desktop.csproj" -c Release -o "%DIST%" --nologo

echo [3/3] Copy runtime assets
if exist "%~dp0agents" xcopy /e /i /q "%~dp0agents" "%DIST%\agents" >nul
if exist "%~dp0skills" xcopy /e /i /q "%~dp0skills" "%DIST%\skills" >nul
if exist "%~dp0models" xcopy /e /i /q "%~dp0models" "%DIST%\models" >nul

echo.
echo Done!  dist/Desktop/
