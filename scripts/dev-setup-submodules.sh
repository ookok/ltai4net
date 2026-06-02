#!/usr/bin/env bash
# LTAI 开发环境子模块设置脚本 (Linux / macOS)
# 用法: ./scripts/dev-setup-submodules.sh
# 作用: 初始化 + sparse-checkout extern/agent-framework (~251MB -> ~27MB)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
AGENT_FW_PATH="$REPO_ROOT/extern/agent-framework"

echo "=== LTAI 子模块设置 ==="
echo "Repo: $REPO_ROOT"
echo "Submodules:"
echo "  - extern/agent-framework      (Microsoft.Agents.AI 全部 dotnet 项目)"
echo "  - extern/durabletask-dotnet   (Microsoft.DurableTask.* 1.24.2 源码, 调试用)"
echo ""

# 1. 初始化所有子模块
echo "[1/4] git submodule update --init --recursive ..."
git -C "$REPO_ROOT" submodule update --init --recursive

# 2. 检测 agent-framework 是否存在
if [ ! -d "$AGENT_FW_PATH/.git" ]; then
  echo "  [error] extern/agent-framework 不可用, 跳过 sparse-checkout" >&2
  exit 1
fi

# 3. 应用 sparse-checkout
echo "[2/4] 应用 MAF sparse-checkout (~251MB -> ~27MB) ..."
git -C "$AGENT_FW_PATH" config core.sparseCheckout true
git -C "$AGENT_FW_PATH" config core.sparseCheckoutCone false

cat > "$AGENT_FW_PATH/.git/info/sparse-checkout" <<'PATTERNS'
/*
!/dotnet/tests/
!/dotnet/samples/
!/dotnet/.github/
!/dotnet/.vscode/
!**/bin/
!**/obj/
!**/*.dll
!**/*.pdb
!**/*.cache
!**/*.cache.json
!**/*.nupkg
!**/*.nupkg.gz
!**/*.nuspec
PATTERNS

# 4. 重新 read-tree
echo "[3/4] git read-tree -mu HEAD ..."
git -C "$AGENT_FW_PATH" read-tree -mu HEAD >/dev/null 2>&1 || true
git -C "$AGENT_FW_PATH" sparse-checkout reapply >/dev/null 2>&1 || true

# 5. 报告 size
echo "[4/4] 验证 ..."
SIZE_BYTES=$(find "$AGENT_FW_PATH" -type f -printf "%s\n" 2>/dev/null | awk '{s+=$1} END {print s+0}')
SIZE_MB=$(awk "BEGIN {printf \"%.1f\", $SIZE_BYTES/1048576}")
echo ""
echo "  ✅ extern/agent-framework 当前占用: ${SIZE_MB} MB"
echo ""
echo "提示: 'dotnet build LTAI.sln' 验证编译。"
echo "提示: 恢复完整 clone: 'cd extern/agent-framework && git sparse-checkout disable && git checkout HEAD -- .'"
