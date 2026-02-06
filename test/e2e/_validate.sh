# Shared validation — sourced by run-*.sh scripts
# Expects OUTPUT_DIR to be set

ERRORS=0

REPORT=$(find "$OUTPUT_DIR" -name 'rca-OrdersApiTests.PostOrder_ReturnsCreated-*.md' | head -1)
if [ -z "$REPORT" ]; then
  echo "FAIL: report file not found"
  ERRORS=$((ERRORS + 1))
else
  echo "✓ Report: $(basename "$REPORT")"
  for section in "## Error" "## Analysis" "TraceId"; do
    if grep -q "$section" "$REPORT"; then
      echo "✓ Contains '$section'"
    else
      echo "FAIL: missing '$section'"
      ERRORS=$((ERRORS + 1))
    fi
  done
fi

INDEX=$(find "$OUTPUT_DIR" -name 'index-*.md' | head -1)
if [ -z "$INDEX" ]; then
  echo "FAIL: index file not found"
  ERRORS=$((ERRORS + 1))
else
  echo "✓ Index: $(basename "$INDEX")"
fi

echo ""
if [ "$ERRORS" -gt 0 ]; then
  echo "FAILED: $ERRORS error(s)"
  exit 1
fi

echo "ALL CHECKS PASSED"
echo ""
echo "--- Report Preview ---"
head -40 "$REPORT"
