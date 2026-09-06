#!/bin/bash
# Drives one interactive ctrader-cli backtest wizard per calendar month for
# XAU_SQZ_V1, 2020-01 through the current month, using run_fold.exp (which
# explicitly answers every wizard prompt rather than trusting --start/--end
# flags, which are silently ignored on this CLI build).
set -uo pipefail

OUT_DIR="$HOME/PEARLROCK-BOTS-REPO/wf_results"
RUN_FOLD="$OUT_DIR/run_fold.exp"
LOG="$OUT_DIR/run_all_folds.log"
: > "$LOG"

START_YEAR=2020
START_MONTH=1
END_YEAR=$(date +%Y)
END_MONTH=$((10#$(date +%m)))

y=$START_YEAR
m=$START_MONTH

pass=0
fail=0

while [ "$y" -lt "$END_YEAR" ] || { [ "$y" -eq "$END_YEAR" ] && [ "$m" -le "$END_MONTH" ]; }; do
  label=$(printf "%04d-%02d" "$y" "$m")
  start_str=$(printf "%04d-%02d-01" "$y" "$m")
  next_m=$((m + 1)); next_y=$y
  if [ "$next_m" -gt 12 ]; then next_m=1; next_y=$((y + 1)); fi
  end_str=$(printf "%04d-%02d-01" "$next_y" "$next_m")

  out_json="$OUT_DIR/fixed_${label}_backtest.json"
  expected_period="01/$(printf "%02d" "$m")/${y} - 01/$(printf "%02d" "$next_m")/${next_y}"

  attempt=1
  ok=0
  while [ "$attempt" -le 2 ] && [ "$ok" -eq 0 ]; do
    echo "=== Fold $label (attempt $attempt): $start_str -> $end_str ===" | tee -a "$LOG"
    rm -f "$out_json"
    "$RUN_FOLD" "$label" "$start_str" "$end_str" "$out_json" >> "$LOG" 2>&1

    if [ -f "$out_json" ]; then
      check=$(python3 -c "
import json, sys
try:
    d = json.load(open('$out_json'))
    period = d.get('main',{}).get('testingPeriod',{}).get('formatted','')
    trades = d.get('tradeStatistics',{}).get('totalTrades',{}).get('all')
    roi = d.get('main',{}).get('roi')
    if '$expected_period' in period:
        print(f'OK|{period}|{trades}|{roi}')
    else:
        print(f'MISMATCH|{period}|{trades}|{roi}')
except Exception as e:
    print(f'ERROR|{e}')
")
      status="${check%%|*}"
      if [ "$status" = "OK" ]; then
        ok=1
        pass=$((pass+1))
        echo "PASS $label: $check" | tee -a "$LOG"
      else
        echo "FAIL $label (attempt $attempt): $check" | tee -a "$LOG"
      fi
    else
      echo "FAIL $label (attempt $attempt): no output file produced" | tee -a "$LOG"
    fi
    attempt=$((attempt+1))
  done

  if [ "$ok" -eq 0 ]; then
    fail=$((fail+1))
    echo "!!! FOLD $label FAILED AFTER RETRIES !!!" | tee -a "$LOG"
  fi

  m=$((m + 1))
  if [ "$m" -gt 12 ]; then m=1; y=$((y + 1)); fi
done

echo "=== DONE: $pass passed, $fail failed ===" | tee -a "$LOG"
