#!/usr/bin/env bash
# LTAI CLI — Linux/macOS 一键安装脚本
set -euo pipefail

LTAI_DIR="${HOME}/.ltai"
CLI_URL="https://github.com/ltai-org/ltai4net/releases/latest/download/ltai"

echo "=== LTAI 安装脚本 ==="
echo ""

# 1. 下载 CLI
mkdir -p "${LTAI_DIR}"
echo "[1/3] 下载 CLI..."
if curl -fsSL "${CLI_URL}" -o "${LTAI_DIR}/ltai" 2>/dev/null; then
    chmod +x "${LTAI_DIR}/ltai"
    echo "  ✅ 已下载: ${LTAI_DIR}/ltai"
else
    echo "  ⚠️ 下载失败，请手动下载到 ${LTAI_DIR}/ltai"
    echo "     下载地址: ${CLI_URL}"
fi

# 2. 添加到 PATH
if [[ ":$PATH:" != *":${LTAI_DIR}:"* ]]; then
    echo 'export PATH="$PATH:'${LTAI_DIR}'"' >> "${HOME}/.bashrc"
    echo 'export PATH="$PATH:'${LTAI_DIR}'"' >> "${HOME}/.zshrc" 2>/dev/null || true
    echo "[2/3] 已添加到 PATH (~/.bashrc)"
    export PATH="${PATH}:${LTAI_DIR}"
else
    echo "[2/3] PATH 已包含 LTAI"
fi

# 3. 下载 ONNX 模型（可选）
MODEL_DIR="${LTAI_DIR}/models/minilm-l6-v2"
if [ ! -d "${MODEL_DIR}" ]; then
    echo "[3/3] 下载嵌入模型 (90MB, 可选)..."
    mkdir -p "${MODEL_DIR}"
    if curl -fsSL "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx" -o "${MODEL_DIR}/model.onnx" 2>/dev/null && \
       curl -fsSL "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt" -o "${MODEL_DIR}/vocab.txt" 2>/dev/null; then
        echo "  ✅ 模型已下载"
    else
        echo "  ⚠️ 模型下载失败，可稍后手动下载"
    fi
else
    echo "[3/3] 模型已存在，跳过"
fi

echo ""
echo "=== 安装完成 ==="
echo "运行 ltai 开始使用"
