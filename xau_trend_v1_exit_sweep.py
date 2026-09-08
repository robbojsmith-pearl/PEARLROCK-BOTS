#!/usr/bin/env python3
"""
Quick check: is XAU_SQZ_V1's exit stack (SL=2x, TP=5x, trail=3.6x ATR) —
inherited wholesale, never validated for THIS strategy's entry — actually
good for XAU_TREND_V1, or just "good enough, borrowed"?

Fixes the already-validated entry design (period=200H, buffer=0, efficiency
gate=0.03, continuous sizing) and sweeps only the exit multiples, selected
on TRAIN (2013-2016) only, frozen and walk-forward tested 2017-2026 —
same discipline as everything else today.
"""

import numpy as np
import pandas as pd
from scipy import stats

M5_CSV = "../cTrader/xauusd_m5_2013_2026.csv"
ATR_PERIOD = 20
RISK_PCT, MAX_LEV, LEV_BUFFER, START_BALANCE = 1.0, 10.0, 0.2, 10000.0
COST_PER_OZ = 0.48
TREND_PERIOD, EFF_WINDOW = 200, 200
GATE_THRESHOLD = 0.03
MIN_FACTOR, MAX_FACTOR = 0.25, 2.0
TRAIN_START, TRAIN_END = "2013-01-01", "2016-12-31"

EXIT_CANDIDATES = [
    (2.0, 5.0, 3.6),   # current (borrowed from XAU_SQZ_V1)
    (1.5, 4.0, 2.5),
    (2.0, 4.0, 3.0),
    (2.5, 5.0, 3.6),
    (2.0, 6.0, 4.0),
    (3.0, 6.0, 4.0),
]

m5 = pd.read_csv(M5_CSV)
m5["timestamp"] = pd.to_datetime(m5["timestamp"], utc=True)
m5 = m5.sort_values("timestamp").set_index("timestamp")
h1 = m5.resample("1h", label="left", closed="left").agg(
    {"open": "first", "high": "max", "low": "min", "close": "last", "volume": "sum"}
).dropna().reset_index()

close = h1["close"].values
high = h1["high"].values
low = h1["low"].values
ts = h1["timestamp"]

sma = pd.Series(close).rolling(TREND_PERIOD).mean().values
net_move = np.abs(pd.Series(close).diff(EFF_WINDOW))
path_len = pd.Series(close).diff().abs().rolling(EFF_WINDOW).sum()
efficiency = (net_move / path_len).values

prev_close = pd.Series(close).shift(1).values
tr = np.maximum.reduce([high - low, np.abs(high - prev_close), np.abs(low - prev_close)])
atr = np.full(len(tr), np.nan)
atr[ATR_PERIOD] = np.nanmean(tr[1:ATR_PERIOD + 1])
for i in range(ATR_PERIOD + 1, len(tr)):
    atr[i] = (atr[i - 1] * (ATR_PERIOD - 1) + tr[i]) / ATR_PERIOD

h1["month"] = ts.dt.to_period("M")
warmup = max(TREND_PERIOD, EFF_WINDOW, ATR_PERIOD) + 5
train_mask = (ts >= TRAIN_START) & (ts <= TRAIN_END)
REF_EFF = np.nanmedian(efficiency[train_mask.values])


def run_backtest(sl_atr, tp_atr, trail_atr, months_filter=None):
    rows = []
    month_list = sorted(h1["month"].unique())
    if months_filter is not None:
        month_list = [m for m in month_list if months_filter(m)]
    for month in month_list:
        idxs = h1.index[h1["month"] == month].to_numpy()
        start_i, end_i = idxs[0], idxs[-1]
        if start_i < warmup:
            continue
        equity = START_BALANCE
        peak_equity = START_BALANCE
        max_dd_pct = 0.0
        position = None
        trades = []
        for i in range(start_i, end_i + 1):
            if np.isnan(sma[i]) or np.isnan(atr[i]) or atr[i] <= 0 or np.isnan(efficiency[i]):
                continue
            if position is not None:
                exit_price = None
                if low[i] <= position["sl_price"]:
                    exit_price = position["sl_price"]
                elif high[i] >= position["tp_price"]:
                    exit_price = position["tp_price"]
                else:
                    cand = close[i] - atr[i] * trail_atr
                    if cand > position["trail_level"]:
                        position["trail_level"] = cand
                    if close[i] < position["trail_level"]:
                        exit_price = close[i]
                if exit_price is not None:
                    pnl = (exit_price - position["entry_price"]) * position["volume"]
                    cost = COST_PER_OZ * position["volume"]
                    equity += (pnl - cost)
                    peak_equity = max(peak_equity, equity)
                    dd = (peak_equity - equity) / peak_equity * 100 if peak_equity > 0 else 0
                    max_dd_pct = max(max_dd_pct, dd)
                    trades.append(pnl - cost)
                    position = None
                else:
                    continue
            if position is None and close[i] > sma[i] and efficiency[i] >= GATE_THRESHOLD:
                entry_price = close[i]
                stop_dist = atr[i] * sl_atr
                tp_dist = atr[i] * tp_atr
                risk_dollars = equity * (RISK_PCT / 100.0)
                raw_vol = risk_dollars / stop_dist
                factor = np.clip(efficiency[i] / REF_EFF, MIN_FACTOR, MAX_FACTOR)
                raw_vol *= factor
                max_lev_vol = (equity * max(0, MAX_LEV - LEV_BUFFER)) / entry_price
                volume = min(raw_vol, max_lev_vol)
                if volume <= 0:
                    continue
                position = {
                    "entry_price": entry_price, "volume": volume,
                    "sl_price": entry_price - stop_dist, "tp_price": entry_price + tp_dist,
                    "trail_level": entry_price - atr[i] * trail_atr,
                }
        if position is not None:
            i = end_i
            pnl = (close[i] - position["entry_price"]) * position["volume"]
            cost = COST_PER_OZ * position["volume"]
            equity += (pnl - cost)
            peak_equity = max(peak_equity, equity)
            dd = (peak_equity - equity) / peak_equity * 100 if peak_equity > 0 else 0
            max_dd_pct = max(max_dd_pct, dd)
            trades.append(pnl - cost)
        roi_pct = (equity - START_BALANCE) / START_BALANCE * 100
        rows.append({"month": str(month), "roi_pct": roi_pct, "totalTrades": len(trades), "maxDD_pct": max_dd_pct})
    return pd.DataFrame(rows)


train_filter = lambda m: TRAIN_START <= str(m) + "-01" <= TRAIN_END
test_filter = lambda m: str(m) + "-01" > TRAIN_END

print("=== Selecting exit multiples on TRAIN ONLY ===")
train_scores = {}
for sl, tp, trail in EXIT_CANDIDATES:
    df = run_backtest(sl, tp, trail, months_filter=train_filter)
    comp = (1 + df["roi_pct"] / 100).prod() if len(df) else float("nan")
    train_scores[(sl, tp, trail)] = comp
    print(f"SL={sl:.1f} TP={tp:.1f} trail={trail:.1f}  train_compounded={comp:.3f}x  "
          f"avg_trades/mo={df['totalTrades'].mean():.1f}")

winner = max(train_scores, key=lambda k: train_scores[k] if not np.isnan(train_scores[k]) else -1)
print(f"\nWinner (train only): SL={winner[0]} TP={winner[1]} trail={winner[2]}")

print(f"\n=== Test period (2017-2026), all candidates (for context) ===")
for sl, tp, trail in EXIT_CANDIDATES:
    df = run_backtest(sl, tp, trail, months_filter=test_filter)
    comp = (1 + df["roi_pct"] / 100).prod()
    tag = "  <-- TRAIN WINNER" if (sl, tp, trail) == winner else ("  <-- CURRENT (borrowed)" if (sl,tp,trail)==(2.0,5.0,3.6) else "")
    print(f"SL={sl:.1f} TP={tp:.1f} trail={trail:.1f}  test_compounded={comp:.3f}x  "
          f"avg_ROI/mo={df['roi_pct'].mean():+.3f}%  avg_maxDD={df['maxDD_pct'].mean():.2f}%{tag}")
