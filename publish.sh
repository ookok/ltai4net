#!/usr/bin/env bash
set -euo pipefail

echo "===== LTAI - Publish All ====="

DIST="$(dirname "$0")/dist"

echo "[1/4] Clean dist"
rm -rf "$DIST"
mkdir -p "$DIST"

echo "[2/4] Restore"
dotnet restore "LTAI.sln" --nologo

echo "[3/4] Publish 4 entry points"
dotnet publish "src/LTAI.Cli/LTAI.Cli.csproj"         -c Release -o "$DIST/CLI"     --nologo
dotnet publish "src/LTAI.TUI/LTAI.TUI.csproj"        -c Release -o "$DIST/TUI"     --nologo
dotnet publish "src/LTAI.Desktop/LTAI.Desktop.csproj" -c Release -o "$DIST/Desktop" --nologo
dotnet publish "src/LTAI.Web/LTAI.Web.csproj"        -c Release -o "$DIST/Web"     --nologo

echo "[4/4] Copy runtime assets"
for dir in CLI TUI Desktop Web; do
    if [ -d "$DIST/$dir" ]; then
        cp -r agents  "$DIST/$dir/agents"  2>/dev/null || true
        cp -r skills  "$DIST/$dir/skills"  2>/dev/null || true
        cp -r models  "$DIST/$dir/models"  2>/dev/null || true
    fi
done

rm -rf "$DIST/lib"

echo ""
echo "Done!"
echo "  dist/CLI/     - dotnet CLI"
echo "  dist/TUI/     - Terminal UI"
echo "  dist/Desktop/ - Avalonia Desktop"
echo "  dist/Web/     - Web API"
