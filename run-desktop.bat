@echo off
chcp 65001 >nul
echo === LTAI - 启动 Desktop ===
dotnet run --project src\LTAI.Desktop
pause
