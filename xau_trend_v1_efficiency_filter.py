#!/usr/bin/env python3
"""
XAU_TREND_V1 + trend-efficiency entry filter.

Isolates ONE new variable: holds period=200H, buffer=0.0 fixed (the
already-established winner) and only sweeps the efficiency threshold, so
the effect of adding this filter can be measured cleanly rather than
re-opening the whole grid and diluting the test with more comparisons.

Entry condition becomes: price > SMA(200) AND efficiency(200H) >= threshold
(efficiency = |net move| / total path length over 200H — ~1 in a clean
trend, ~0 in chop, independent of volatility level; see
xau_trend_v1_garch_regime_check.py for why this was chosen over GARCH vol).

Same protocol as before: select the threshold on 2013-2016 ONLY, freeze it,
walk forward unchanged through 2017-2026, then compare against both the
unfiltered XAU_TREND_V1 and XAU_SQZ_V1's real cost-adjusted trades.
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
TREND_PERIOD = 200
EFF_WINDOW = 200
THRESHOLDS = [0.0, 0.03, 0.05, 0.07, 0.10, 0.14]
TRAIN_START, TRAIN_END = "2013-01-01", "2016-12-31"

print("Loading full history...")
m5 = pd.read_csv(M5_CSV)
m5["timestamp"] = pd.to_datetime(m5["timestamp"], utc=True)
m5 = m5.sort_values("timestamp").set_index("timestamp")
h1 = m5.resample("1h", label="left", closed="left").agg(
    {"open": "first", "high": "max", "low": "min", "close": "last", "volume": "sum"}
).dropna().reset_index()
print(f"H1 bars: {len(h1)}")

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


def run_backtest(eff_threshold, months_filter=None):
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
            if position is None and close[i] > sma[i] and efficiency[i] >= eff_threshold:
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

print(f"\n=== Selecting efficiency threshold on TRAIN ONLY ({TRAIN_START} -> {TRAIN_END}), period=200H buffer=0 fixed ===")
train_scores = {}
for thresh in THRESHOLDS:
    df = run_backtest(thresh, months_filter=train_filter)
    comp = (1 + df["roi_pct"] / 100).prod() if len(df) else float("nan")
    train_scores[thresh] = comp
    print(f"eff_threshold={thresh:.2f}  train_compounded={comp:.3f}x  n_months={len(df)}  "
          f"avg_trades/mo={df['totalTrades'].mean() if len(df) else float('nan'):.1f}")

winner = max(train_scores, key=lambda k: train_scores[k] if not np.isnan(train_scores[k]) else -1)
print(f"\nWinner (train only): eff_threshold={winner} (train compounded {train_scores[winner]:.3f}x)")

print(f"\n=== Frozen threshold={winner}, walk-forward {TRAIN_END} onward (never touched during selection) ===")
test_df = run_backtest(winner, months_filter=test_filter)
test_df.to_csv("xau_trend_v1_efffilter_test_monthly.csv", index=False)
comp = (1 + test_df["roi_pct"] / 100).prod()
print(f"Compounded: {comp:.3f}x   avg ROI/mo={test_df['roi_pct'].mean():+.3f}%   "
      f"%%positive={100*(test_df['roi_pct']>0).mean():.1f}%   avg trades/mo={test_df['totalTrades'].mean():.1f}   "
      f"avg maxDD={test_df['maxDD_pct'].mean():.2f}%")

print("\n=== By regime sub-window (filtered vs unfiltered XAU_TREND_V1) ===")
unfiltered = pd.read_csv("xau_trend_v1_fullhistory_test_monthly.csv")
windows = [
    ("2017-2019 chop", "2017-01", "2019-12"),
    ("2020 COVID vol", "2020-01", "2020-12"),
    ("2021-2022 pullback", "2021-01", "2022-12"),
    ("2023-2026 bull", "2023-01", "2026-12"),
]
for label, start, end in windows:
    sub_f = test_df[(test_df["month"] >= start) & (test_df["month"] <= end)]
    sub_u = unfiltered[(unfiltered["month"] >= start) & (unfiltered["month"] <= end)]
    if len(sub_f) == 0:
        continue
    cf = (1 + sub_f["roi_pct"] / 100).prod()
    cu = (1 + sub_u["roi_pct"] / 100).prod()
    print(f"{label:22s} n={len(sub_f):3d}  filtered: compounded={cf:.3f}x avg_ROI/mo={sub_f['roi_pct'].mean():+.3f}% trades/mo={sub_f['totalTrades'].mean():.1f}  |  "
          f"unfiltered: compounded={cu:.3f}x avg_ROI/mo={sub_u['roi_pct'].mean():+.3f}%")

print("\n=== vs unfiltered XAU_TREND_V1, full test period ===")
merged_uf = unfiltered.merge(test_df, on="month", suffixes=("_unfiltered", "_filtered"))
t, p = stats.ttest_rel(merged_uf["roi_pct_filtered"], merged_uf["roi_pct_unfiltered"])
print(f"n={len(merged_uf)}  unfiltered compounded={(1+merged_uf['roi_pct_unfiltered']/100).prod():.3f}x  "
      f"filtered compounded={(1+merged_uf['roi_pct_filtered']/100).prod():.3f}x  paired t={t:.3f} p={p:.4f}")

print("\n=== vs XAU_SQZ_V1 real trades, overlapping months ===")
bot_rows = []
for fp in sorted(glob.glob("wf_results/fixed_*.json")):
    with open(fp) as f:
        d = json.load(f)
    month = pd.to_datetime(d["main"]["testingPeriod"]["formatted"].split("(")[1].split(" - ")[0],
                            format="%d/%m/%Y").to_period("M")
    equity = d["main"]["startingCapital"]
    n_trades = 0
    for t_ in d["history"]["items"]:
        equity += (t_["net"] - COST_PER_OZ * t_["volume"])
        n_trades += 1
    roi_pct = (equity - d["main"]["startingCapital"]) / d["main"]["startingCapital"] * 100
    bot_rows.append({"month": str(month), "roi_pct": roi_pct, "totalTrades": n_trades})
bot_df = pd.DataFrame(bot_rows)
merged_bot = bot_df.merge(test_df, on="month", suffixes=("_bot", "_filtered"))
t2, p2 = stats.ttest_rel(merged_bot["roi_pct_bot"], merged_bot["roi_pct_filtered"])
print(f"n={len(merged_bot)}  bot compounded={(1+merged_bot['roi_pct_bot']/100).prod():.3f}x  "
      f"filtered_trend compounded={(1+merged_bot['roi_pct_filtered']/100).prod():.3f}x  paired t={t2:.3f} p={p2:.4f}")

print("\nSaved: xau_trend_v1_efffilter_test_monthly.csv")
