@echo off
chcp 65001 >nul
echo === LTAI - 启动 Web API ===
dotnet run --project src\LTAI.Web
pause
