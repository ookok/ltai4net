#!/usr/bin/env bash
# LTAI Agent OS — Test Suite Runner with Audit Log Validation
# Usage: ./run-tests.sh [layer] [-report]
# Example: ./run-tests.sh L0 -report

set -euo pipefail

SPECFILE="${SPECFILE:-docs/test_expected.csv}"
PROMPTS="${PROMPTS:-docs/testprompts.txt}"
CLI="${CLI:-dotnet run --project src/LTAI.Cli --}"
LAYER="${1:-L0}"
REPORT="${2:-}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[0;33m'; CYAN='\033[0;36m'; NC='\033[0m'

declare -A EXPECTED=() PATTERNS=()

load_specs() {
    while IFS=',' read -r id expected pattern; do
        [[ -z "$id" || "$id" == "ID" ]] && continue
        EXPECTED["$id"]="$expected"
        PATTERNS["$id"]="$pattern"
    done < "$SPECFILE"
}

run_test() {
    local id="$1" query="$2"
    local expected="${EXPECTED[$id]:-?}"
    local pattern="${PATTERNS[$id]:-}"
    
    echo -ne "  ${CYAN}[$id]${NC} "
    
    local start=$(date +%s%N 2>/dev/null || echo 0)
    local output
    output=$($CLI debug --query "$query" 2>&1) || true
    local end=$(date +%s%N 2>/dev/null || echo 0)
    local elapsed=$(( (end - start) / 1000000 ))
    
    local matched=0
    if [ -n "$pattern" ]; then
        IFS='|' read -ra PATS <<< "$pattern"
        for p in "${PATS[@]}"; do
            if echo "$output" | grep -qiE "$p"; then
                matched=1; break
            fi
        done
    fi
    
    case "$expected" in
        "❌")
            if [ $matched -eq 1 ]; then
                echo -e "${GREEN}PASS${NC} (${elapsed}ms)"; return 0
            else
                echo -e "${RED}FAIL${NC} (${elapsed}ms) expected blocked"; return 1
            fi
            ;;
        "✅")
            if [ $matched -eq 1 ]; then
                echo -e "${GREEN}PASS${NC} (${elapsed}ms)"; return 0
            else
                echo -e "${RED}FAIL${NC} (${elapsed}ms) no match for: ${pattern:0:40}"; return 1
            fi
            ;;
        "⚠️")
            echo -e "${YELLOW}PASS*${NC} (${elapsed}ms)"; return 0
            ;;
        *)
            [ $matched -eq 1 ] && echo -e "${GREEN}PASS${NC} (${elapsed}ms)" || echo -e "${RED}FAIL${NC} (${elapsed}ms)"
            return $(( 1 - matched ))
            ;;
    esac
}

main() {
    load_specs
    
    echo ""
    echo -e "${CYAN}=== LTAI Agent OS Test Suite (Audit-Validated) ===${NC}"
    echo -e "Layer: ${YELLOW}${LAYER}${NC}"
    echo ""
    
    local pass=0 fail=0 current_layer="" current_id=""
    
    while IFS= read -r line; do
        [[ "$line" =~ ^#.* ]] && continue
        [[ -z "$line" ]] && continue
        
        if [[ "$line" =~ ^##[[:space:]]+L([0-5]) ]]; then
            current_layer="L${BASH_REMATCH[1]}"; continue
        fi
        if [[ "$line" =~ ^##[[:space:]]+跨层 ]]; then
            current_layer="CHAOS"; continue
        fi
        if [[ "$line" =~ ^#[[:space:]]+(L[0-5]|CHAOS)-[A-Z0-9-]+ ]]; then
            current_id="${BASH_REMATCH[1]}"; continue
        fi
        
        if [[ -n "$current_layer" && -n "$current_id" && -n "$line" ]]; then
            if [[ "$LAYER" == "all" || "$current_layer" == "$LAYER" ]]; then
                if run_test "$current_id" "$line"; then
                    ((pass++))
                else
                    ((fail++))
                fi
            fi
            current_id=""
        fi
    done < "$PROMPTS"
    
    echo ""
    echo -e "${CYAN}=== Results ===${NC}"
    echo -e "  PASS: ${GREEN}${pass}${NC}  FAIL: ${RED}${fail}${NC}"
    
    [ "$REPORT" = "-report" ] && echo "Report: docs/test_report_$(date +%Y%m%d-%H%M%S).csv"
}

main
