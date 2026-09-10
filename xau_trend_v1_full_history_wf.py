#!/usr/bin/env python3
"""
XAU_TREND_V1 full-history validation (2013-2026).

Protocol:
  1. Select {period, buffer} on 2013-01-01 -> 2016-12-31 ONLY (bear market +
     early chop — a deliberately different, harder regime than the 2021-2026
     bull window everything else today has been tested on).
  2. Freeze those params. Run them unchanged as a monthly walk-forward across
     2017-01-01 through the end of the available data — no re-optimization,
     same discipline already used for XAU_SQZ_V1's own validation.
  3. Compare against XAU_SQZ_V1's real wf_results trades over the overlapping
     portion (2020-01 onward, since that's as far back as the bot's own
     walk-forward JSON reports go).
  4. Break test-period performance out by sub-window (2017-2019 chop,
     2020 COVID vol, 2021-2022 pullback, 2023-2026 bull) so a regime-specific
     failure is visible rather than averaged away.

Same execution/cost assumptions as every prior test today: SL=2xATR (broker,
intrabar), TP=5xATR (broker, intrabar), trail=3.6xATR (software, bar-close),
1% risk-based sizing, 9.8x effective leverage cap, $0.48/oz round-trip cost,
$10k independent monthly folds.
"""

import glob
import json
import numpy as np
import pandas as pd
from scipy import stats

M5_CSV = "../cTrader/xauusd_m5_2013_2026.csv"
ATR_PERIOD = 20
SL_ATR, TP_ATR, TRAIL_ATR = 2.0, 5.0, 3.6
RISK_PCT, MAX_LEV, LEV_BUFFER, START_BALANCE = 1.0, 10.0, 0.2, 10000.0
COST_PER_OZ = 0.48
PERIODS = [100, 150, 200, 250]
BUFFERS = [0.0, 0.5, 1.0]
TRAIN_START, TRAIN_END = "2013-01-01", "2016-12-31"

print(f"Loading {M5_CSV} ...")
m5 = pd.read_csv(M5_CSV)
m5["timestamp"] = pd.to_datetime(m5["timestamp"], utc=True)
m5 = m5.sort_values("timestamp").set_index("timestamp")
print(f"M5 range: {m5.index[0]} -> {m5.index[-1]}  ({len(m5)} bars)")

h1 = m5.resample("1h", label="left", closed="left").agg(
    {"open": "first", "high": "max", "low": "min", "close": "last", "volume": "sum"}
).dropna().reset_index()
print(f"H1 bars: {len(h1)}")

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


def run_backtest(trend_period, buffer_mult, months_filter=None):
    sma = pd.Series(close).rolling(trend_period).mean().values
    warmup = max(trend_period, ATR_PERIOD) + 5
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
            if np.isnan(sma[i]) or np.isnan(atr[i]) or atr[i] <= 0:
                continue
            if position is not None:
                exit_price = None
                if low[i] <= position["sl_price"]:
                    exit_price = position["sl_price"]
                elif high[i] >= position["tp_price"]:
                    exit_price = position["tp_price"]
                else:
                    cand = close[i] - atr[i] * TRAIL_ATR
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
            if position is None and (close[i] - sma[i]) > buffer_mult * atr[i]:
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


train_filter = lambda m: TRAIN_START <= str(m) + "-01" <= TRAIN_END
test_filter = lambda m: str(m) + "-01" > TRAIN_END

print(f"\n=== Selecting {{period, buffer}} on TRAIN ONLY ({TRAIN_START} -> {TRAIN_END}) ===")
train_scores = {}
for p in PERIODS:
    for b in BUFFERS:
        df = run_backtest(p, b, months_filter=train_filter)
        comp = (1 + df["roi_pct"] / 100).prod() if len(df) else float("nan")
        train_scores[(p, b)] = comp
        print(f"period={p:4d}H buffer={b:.1f}  train_compounded={comp:.3f}x  n_months={len(df)}  "
              f"avg_trades/mo={df['totalTrades'].mean() if len(df) else float('nan'):.1f}")

winner = max(train_scores, key=lambda k: train_scores[k] if not np.isnan(train_scores[k]) else -1)
print(f"\nWinner (train only): period={winner[0]}H buffer={winner[1]}xATR (train compounded {train_scores[winner]:.3f}x)")

print(f"\n=== Running frozen winner as monthly walk-forward, {TRAIN_END} onward (never touched during selection) ===")
test_df = run_backtest(winner[0], winner[1], months_filter=test_filter)
test_df.to_csv("xau_trend_v1_fullhistory_test_monthly.csv", index=False)
comp = (1 + test_df["roi_pct"] / 100).prod()
print(f"Test period: {test_df['month'].iloc[0]} -> {test_df['month'].iloc[-1]}  n_months={len(test_df)}")
print(f"Compounded: {comp:.3f}x   avg ROI/mo={test_df['roi_pct'].mean():+.3f}%   "
      f"%%positive={100*(test_df['roi_pct']>0).mean():.1f}%   avg trades/mo={test_df['totalTrades'].mean():.1f}   "
      f"avg maxDD={test_df['maxDD_pct'].mean():.2f}%")

# ---------------------------------------------------------------------------
# Break out by regime sub-window
# ---------------------------------------------------------------------------
print("\n=== By regime sub-window ===")
windows = [
    ("2017-2019 chop", "2017-01", "2019-12"),
    ("2020 COVID vol", "2020-01", "2020-12"),
    ("2021-2022 pullback", "2021-01", "2022-12"),
    ("2023-2026 bull", "2023-01", "2026-12"),
]
for label, start, end in windows:
    sub = test_df[(test_df["month"] >= start) & (test_df["month"] <= end)]
    if len(sub) == 0:
        continue
    c = (1 + sub["roi_pct"] / 100).prod()
    print(f"{label:22s} n={len(sub):3d}  compounded={c:.3f}x  avg_ROI/mo={sub['roi_pct'].mean():+.3f}%  "
          f"%%pos={100*(sub['roi_pct']>0).mean():.1f}%  avg_trades/mo={sub['totalTrades'].mean():.1f}")

# ---------------------------------------------------------------------------
# Compare to XAU_SQZ_V1 real trades, overlapping months only (bot data starts 2020-01)
# ---------------------------------------------------------------------------
print("\n=== vs XAU_SQZ_V1 real trades (cost-adjusted, overlapping months from 2020-01) ===")
bot_rows = []
for fp in sorted(glob.glob("wf_results/fixed_*.json")):
    with open(fp) as f:
        d = json.load(f)
    month = pd.to_datetime(d["main"]["testingPeriod"]["formatted"].split("(")[1].split(" - ")[0],
                            format="%d/%m/%Y").to_period("M")
    equity = d["main"]["startingCapital"]
    n_trades = 0
    for t in d["history"]["items"]:
        equity += (t["net"] - COST_PER_OZ * t["volume"])
        n_trades += 1
    roi_pct = (equity - d["main"]["startingCapital"]) / d["main"]["startingCapital"] * 100
    bot_rows.append({"month": str(month), "roi_pct": roi_pct, "totalTrades": n_trades})
bot_df = pd.DataFrame(bot_rows)

merged = bot_df.merge(test_df, on="month", suffixes=("_bot", "_trend"))
print(f"Overlapping months: {len(merged)}")
bot_comp = (1 + merged["roi_pct_bot"] / 100).prod()
trend_comp = (1 + merged["roi_pct_trend"] / 100).prod()
t, p = stats.ttest_rel(merged["roi_pct_bot"], merged["roi_pct_trend"])
print(f"XAU_SQZ_V1:   compounded={bot_comp:.3f}x  avg ROI/mo={merged['roi_pct_bot'].mean():+.3f}%  "
      f"avg trades/mo={merged['totalTrades_bot'].mean():.1f}")
print(f"XAU_TREND_V1: compounded={trend_comp:.3f}x  avg ROI/mo={merged['roi_pct_trend'].mean():+.3f}%  "
      f"avg trades/mo={merged['totalTrades_trend'].mean():.1f}")
print(f"Paired t-test on monthly ROI%, n={len(merged)}: t={t:.3f} p={p:.4f}")

merged.to_csv("xau_trend_v1_fullhistory_vs_bot.csv", index=False)
print("\nSaved: xau_trend_v1_fullhistory_test_monthly.csv, xau_trend_v1_fullhistory_vs_bot.csv")
