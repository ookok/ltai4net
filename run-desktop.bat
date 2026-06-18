@echo off
chcp 65001 >nul
echo === LTAI - 启动 Desktop ===
if not exist ".env" (
    if exist ".env.example" (
        copy .env.example .env >nul
        echo [提示] 请编辑 .env 文件，填入你的 API Key 后重新运行。
        notepad .env
        exit /b 1
    )
)
dotnet run --project src\LTAI.Desktop %*
if NOT "%CI%"=="true" pause
