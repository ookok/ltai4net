@echo off
chcp 65001 >nul
echo === LTAI - 启动 Desktop ===
dotnet run --project src\LTAI.Desktop
if NOT "%CI%"=="true" pause
