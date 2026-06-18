@echo off
chcp 65001 >nul
echo === LTAI - 启动 TUI ===
if not exist ".env" (
    if exist ".env.example" (
        echo [提示] 未检测到 .env 文件。正在从 .env.example 创建模板...
        copy .env.example .env >nul
        echo [提示] 请编辑 .env 文件，填入你的 API Key 后重新运行。
        echo.
        notepad .env
    ) else (
        echo [提示] 未检测到 .env 文件。请设置环境变量 DEEPSEEK_API_KEY 或创建 .env 文件。
    )
)
dotnet run --project src\LTAI.TUI %*
if NOT "%CI%"=="true" pause
