#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
MODEL="${1:-qwen2.5:3b}"
OUTPUT_DIR="$SCRIPT_DIR/output"
FIXTURES_DIR="$SCRIPT_DIR/fixtures"

echo "=== RCA E2E: Ollama ==="
echo "Model: $MODEL"

# Check Ollama
if ! curl -sf http://localhost:11434/api/tags >/dev/null 2>&1; then
  echo "ERROR: Ollama not running. Start with: ollama serve"
  exit 1
fi
echo "✓ Ollama running"

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo ""
dotnet run --project "$ROOT_DIR/src/Rca" -- analyze "$FIXTURES_DIR" \
  --provider ollama \
  --model "$MODEL" \
  --output-dir "$OUTPUT_DIR" \
  --timeout 120
echo ""

source "$SCRIPT_DIR/_validate.sh"
