#!/usr/bin/env bash
# ═══════════════════════════════════════════════
# LTAI 一键测试脚本 — Linux / macOS / WSL
# ═══════════════════════════════════════════════
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'; NC='\033[0m'
STEP=0

next() { STEP=$((STEP+1)); echo -e "\n${CYAN}═══ [$STEP/$TOTAL] $1 ═══${NC}"; }

# 依赖检查
for cmd in dotnet reportgenerator; do
    if ! command -v "$cmd" &>/dev/null; then
        echo -e "${RED}缺少依赖: $cmd${NC}"
        echo "  dotnet: https://dotnet.microsoft.com/download"
        echo "  reportgenerator: dotnet tool install -g dotnet-reportgenerator-globaltool"
        exit 1
    fi
done

TOTAL=6

# ── 1. 清理 ──
next "清理上次构建"
rm -rf coverage-report TestResults
dotnet clean LTAI.sln -c Release -q 2>/dev/null || true

# ── 2. 还原 ──
next "还原 NuGet 包"
dotnet restore LTAI.sln

# ── 3. 构建 ──
next "编译所有项目"
dotnet build LTAI.sln -c Release --no-restore -warnaserror

# ── 4. 运行全部测试 + 覆盖率 ──
next "运行全部测试（并行 xUnit）"
dotnet test LTAI.sln -c Release --no-build \
    --settings tests/runsettings.xml \
    --collect:"XPlat Code Coverage" \
    --logger "trx;LogFileName=test-results.trx" \
    -m:4 \
    || echo -e "${RED}⚠ 部分测试失败 — 查看上方详情${NC}"

# ── 5. 覆盖率报告 ──
next "生成覆盖率报告"
reportgenerator \
    -reports:tests/**/TestResults/**/coverage.cobertura.xml \
    -targetdir:coverage-report \
    -reporttypes:Html

echo -e "${GREEN}✅ 覆盖率报告: coverage-report/index.html${NC}"

# ── 6. 汇总 ──
next "汇总"
echo -e "${GREEN}════════════════════════════════════════${NC}"
echo -e "${GREEN}  全部完成${NC}"
echo -e "${GREEN}  ⚡ 测试结果:  tests/**/TestResults/test-results.trx${NC}"
echo -e "${GREEN}  📊 覆盖率:    coverage-report/index.html${NC}"
echo -e "${GREEN}  💡 负载测试:  k6 run tests/loadtest/loadtest.js --vus 10 --duration 30s${NC}"
echo -e "${GREEN}════════════════════════════════════════${NC}"
