@echo off
chcp 65001 >nul
echo === LTAI - 启动 TUI ===
dotnet run --project src\LTAI.TUI
if NOT "%CI%"=="true" pause
