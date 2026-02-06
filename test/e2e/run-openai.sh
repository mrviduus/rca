#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
MODEL="${1:-gpt-5-nano}"
OUTPUT_DIR="$SCRIPT_DIR/output"
FIXTURES_DIR="$SCRIPT_DIR/fixtures"

echo "=== RCA E2E: OpenAI ==="
echo "Model: $MODEL"

# Check API key
if [ -z "${OPENAI_API_KEY:-}" ]; then
  echo "ERROR: OPENAI_API_KEY not set"
  exit 1
fi
echo "✓ OPENAI_API_KEY set"

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo ""
dotnet run --project "$ROOT_DIR/src/Rca" -- analyze "$FIXTURES_DIR" \
  --provider openai \
  --model "$MODEL" \
  --output-dir "$OUTPUT_DIR" \
  --timeout 60
echo ""

source "$SCRIPT_DIR/_validate.sh"
