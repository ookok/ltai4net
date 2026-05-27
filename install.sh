#!/usr/bin/env bash
# LTAI Agent OS — One-Line CLI Installer
# Usage: curl -fsSL https://raw.githubusercontent.com/ookok/ltai4net/main/install.sh | bash

set -euo pipefail

REPO="ookok/ltai4net"
VERSION="${LTAI_VERSION:-latest}"
INSTALL_DIR="${LTAI_INSTALL_DIR:-$HOME/.ltai}"
BIN_DIR="${LTAI_BIN_DIR:-/usr/local/bin}"

# ── color ──────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'
BOLD='\033[1m'; NC='\033[0m'

info()  { echo -e "${CYAN}→${NC} $*"; }
ok()    { echo -e "${GREEN}✓${NC} $*"; }
err()   { echo -e "${RED}✗${NC} $*"; exit 1; }

# ── detect platform ────────────────────────────────────
detect_platform() {
    local os arch
    case "$(uname -s)" in
        Linux)  os="linux" ;;
        Darwin) os="macos" ;;
        MINGW*|MSYS*|CYGWIN*) os="windows" ;;
        *) err "Unsupported OS: $(uname -s)" ;;
    esac

    case "$(uname -m)" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
        *) err "Unsupported arch: $(uname -m)" ;;
    esac

    echo "${os}-${arch}"
}

# ── download ───────────────────────────────────────────
download_cli() {
    local platform="$1"
    local ext=""
    [ "$platform" = windows-* ] && ext=".exe"

    local filename="ltai-${platform}${ext}"
    local url

    if [ "$VERSION" = "latest" ]; then
        url="https://github.com/${REPO}/releases/latest/download/${filename}"
    else
        url="https://github.com/${REPO}/releases/download/${VERSION}/${filename}"
    fi

    info "Downloading LTAI CLI ${VERSION} for ${platform}..."
    info "  ${url}"

    mkdir -p "${INSTALL_DIR}/bin"

    if command -v curl &>/dev/null; then
        curl -fSL --progress-bar "$url" -o "${INSTALL_DIR}/bin/ltai${ext}"
    elif command -v wget &>/dev/null; then
        wget -q --show-progress "$url" -O "${INSTALL_DIR}/bin/ltai${ext}"
    else
        err "Neither curl nor wget found. Install one and retry."
    fi

    chmod +x "${INSTALL_DIR}/bin/ltai${ext}"
    ok "Downloaded to ${INSTALL_DIR}/bin/ltai${ext}"
}

# ── link ───────────────────────────────────────────────
link_cli() {
    local platform="$1"
    local ext=""
    [ "$platform" = windows-* ] && ext=".exe"

    local src="${INSTALL_DIR}/bin/ltai${ext}"
    local dst="${BIN_DIR}/ltai${ext}"

    if [ -w "${BIN_DIR}" ] || [ "$(id -u)" = "0" ]; then
        ln -sf "$src" "$dst" 2>/dev/null || cp "$src" "$dst"
        ok "Linked to ${dst}"
    else
        info "Add to PATH: export PATH=\"${INSTALL_DIR}/bin:\$PATH\""
        info "Or run: sudo ln -sf ${src} ${dst}"
    fi
}

# ── main ───────────────────────────────────────────────
main() {
    echo ""
    echo -e "${BOLD}${CYAN}  LTAI Agent OS — CLI Installer${NC}"
    echo -e "  ${BOLD}V1.0${NC}  |  ${REPO}"
    echo ""

    local platform
    platform=$(detect_platform)
    info "Platform: ${platform}"
    info "Install:  ${INSTALL_DIR}"

    download_cli "$platform"
    link_cli "$platform"

    echo ""
    ok "LTAI CLI installed successfully!"
    echo ""
    echo -e "  ${BOLD}Next steps:${NC}"
    echo -e "    ltai init          Configure your environment"
    echo -e "    ltai install       Download core runtime"
    echo -e "    ltai up            Start TUI"
    echo ""

    # auto-run init if first install
    local ext=""
    [ "$platform" = windows-* ] && ext=".exe"
    if [ -t 0 ]; then
        read -rp "  Run 'ltai init' now? [Y/n] " answer
        if [ "$answer" != "n" ] && [ "$answer" != "N" ]; then
            "${INSTALL_DIR}/bin/ltai${ext}" init
        fi
    fi
}

main "$@"
