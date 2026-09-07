#!/usr/bin/env python3
"""
XAU_TREND_V1 — long-only 200H(-ish) trend bot, backtested against a small
grid of {trend period, whipsaw buffer} and compared to XAU_SQZ_V1's real,
cost-adjusted walk-forward.

Design (per today's evidence):
  - LONG ONLY. The period sweep showed short is weak/inconsistent across
    every lookback tested on this 2021-2026 window (gold's structural
    bull market) — fighting to keep shorts in would just be re-adding
    noise the data doesn't support.
  - Entry: price > SMA(period) AND (price - SMA) > buffer_mult * ATR(20)
    (buffer=0 reproduces the pure crossover; buffer>0 tests whether
    the whipsaw-reduction idea from the design discussion actually helps
    once real cost is applied).
  - Exit: identical to XAU_SQZ_V1's validated exit stack — SL=2xATR
    (broker-side, intrabar), TP=5xATR (broker-side, intrabar), ratcheting
    3.6xATR trail (software, bar-close). No momentum-fade exit.
  - Sizing: identical 1% risk-based formula, 9.8x effective leverage cap.
  - Real cost: $0.48/oz round trip (this account's actual spread).
  - Same $10k-per-month independent-fold methodology as every prior test
    today, for direct comparability.

Grid: period in {150, 200}, buffer in {0, 0.5, 1.0} ATR.
"""

import numpy as np
import pandas as pd
from scipy import stats

M5_CSV = "../cTrader/xauusd_m5_5y.sorted.csv"
ATR_PERIOD = 20
SL_ATR, TP_ATR, TRAIL_ATR = 2.0, 5.0, 3.6
RISK_PCT, MAX_LEV, LEV_BUFFER, START_BALANCE = 1.0, 10.0, 0.2, 10000.0
COST_PER_OZ = 0.48

PERIODS = [150, 200]
BUFFERS = [0.0, 0.5, 1.0]

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

prev_close = pd.Series(close).shift(1).values
tr = np.maximum.reduce([high - low, np.abs(high - prev_close), np.abs(low - prev_close)])
atr = np.full(len(tr), np.nan)
atr[ATR_PERIOD] = np.nanmean(tr[1:ATR_PERIOD + 1])
for i in range(ATR_PERIOD + 1, len(tr)):
    atr[i] = (atr[i - 1] * (ATR_PERIOD - 1) + tr[i]) / ATR_PERIOD

h1["month"] = ts.dt.to_period("M")


def run_backtest(trend_period, buffer_mult):
    sma = pd.Series(close).rolling(trend_period).mean().values
    warmup = max(trend_period, ATR_PERIOD) + 5
    rows = []

    for month in sorted(h1["month"].unique()):
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
            if np.isnan(sma[i]) or np.isnan(atr[i]) or atr[i] <= 0:
                continue

            if position is not None:
                exit_price = None
                hit_sl = low[i] <= position["sl_price"]
                hit_tp = high[i] >= position["tp_price"]
                if hit_sl:
                    exit_price = position["sl_price"]
                elif hit_tp:
                    exit_price = position["tp_price"]
                else:
                    atr_buf = atr[i] * TRAIL_ATR
                    cand = close[i] - atr_buf
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

            if position is None:
                above_buffer = (close[i] - sma[i]) > buffer_mult * atr[i]
                if above_buffer:
                    entry_price = close[i]
                    stop_dist = atr[i] * SL_ATR
                    tp_dist = atr[i] * TP_ATR
                    risk_dollars = equity * (RISK_PCT / 100.0)
                    raw_vol = risk_dollars / stop_dist
                    max_lev_vol = (equity * max(0, MAX_LEV - LEV_BUFFER)) / entry_price
                    volume = min(raw_vol, max_lev_vol)
                    if volume <= 0:
                        continue
                    position = {
                        "entry_price": entry_price, "volume": volume,
                        "sl_price": entry_price - stop_dist, "tp_price": entry_price + tp_dist,
                        "trail_level": entry_price - atr[i] * TRAIL_ATR,
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


print("Running grid...")
results = {}
for period in PERIODS:
    for buf in BUFFERS:
        df = run_backtest(period, buf)
        results[(period, buf)] = df
        comp = (1 + df["roi_pct"] / 100).prod()
        print(f"period={period:4d}H  buffer={buf:.1f}xATR  n_folds={len(df):3d}  "
              f"compounded={comp:.3f}x  avg_trades/mo={df['totalTrades'].mean():5.1f}  "
              f"avg_ROI/mo={df['roi_pct'].mean():+.3f}%  %%pos={100*(df['roi_pct']>0).mean():.1f}%  "
              f"avg_maxDD={df['maxDD_pct'].mean():.2f}%")

# ---------------------------------------------------------------------------
# Best-looking candidate vs XAU_SQZ_V1's real, cost-adjusted numbers
# ---------------------------------------------------------------------------
comps = {k: (1 + v["roi_pct"] / 100).prod() for k, v in results.items()}
best_key = max(comps, key=comps.get)
print(f"\nBest by raw compounded return: period={best_key[0]}H buffer={best_key[1]}xATR -> {comps[best_key]:.3f}x")

bot_cost_adj = pd.read_csv("xau_sqz_v1_cost_adjusted_comparison.csv")
bot_comp = (1 + bot_cost_adj["roi_pct_bot"] / 100).prod()
print(f"XAU_SQZ_V1 (real trades, $0.48/oz cost, same months): {bot_comp:.3f}x, "
      f"n_months={len(bot_cost_adj)}, avg_trades/mo={bot_cost_adj['totalTrades_bot'].mean():.1f}")

for key in [best_key, (200, 0.0), (200, 0.5)]:
    df = results[key]
    df2 = df.copy()
    df2["month"] = df2["month"].astype(str)
    merged = bot_cost_adj.merge(df2, on="month", suffixes=("_bot", "_trend"))
    t, p = stats.ttest_rel(merged["roi_pct_bot"], merged["roi_pct"])
    print(f"\nperiod={key[0]}H buffer={key[1]}xATR vs bot, n={len(merged)} overlapping months: "
          f"paired t={t:.3f} p={p:.4f}")
    print(f"  bot avg ROI/mo={merged['roi_pct_bot'].mean():+.3f}%   trend avg ROI/mo={merged['roi_pct'].mean():+.3f}%")

for (period, buf), df in results.items():
    df.to_csv(f"xau_trend_v1_p{period}_b{buf}.csv", index=False)
print("\nSaved per-combo CSVs: xau_trend_v1_p{period}_b{buffer}.csv")
