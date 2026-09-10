#!/usr/bin/env python3
"""
Cost-adjusted comparison of XAU_SQZ_V1 (real) vs the no-squeeze/momentum
trend-only baseline.

Discovery that changed the plan: the bot's own wf_results/*.json backtests
were run with spread.value=0 and commissions.value=0 (only swap is real) —
so they are NOT cost-inclusive as previously assumed. Both series are
therefore frictionless in the same way, and a fair comparison means adding
the SAME realistic per-ounce round-trip cost to both trade logs, not just
the baseline.

Bot side: rebuilt fold-by-fold from wf_results/fixed_*.json history.items
(real trades, real volumes in oz), replaying each fold's $10k-start equity
curve with an added cost per trade = ROUND_TRIP_COST_PER_OZ * volume.

Baseline side: xau_sqz_v1_baseline_notimingfilter_monthly.csv's per-fold
trade list, regenerated here with the same cost applied.

Sweeps a few plausible round-trip cost levels since the true number isn't
known precisely — the conclusion shouldn't hinge on one guess.
"""

import json
import glob
import numpy as np
import pandas as pd
from scipy import stats

COST_LEVELS = [0.48]  # $ per oz, round trip — real figure: $0.24/oz each way on this account

# ---------------------------------------------------------------------------
# Bot: rebuild each fold's equity curve from real trade history + added cost
# ---------------------------------------------------------------------------
def bot_fold_roi(cost_per_oz):
    rows = []
    for fp in sorted(glob.glob("wf_results/fixed_*.json")):
        with open(fp) as f:
            d = json.load(f)
        month = d["main"]["testingPeriod"]["formatted"].split("(")[1].split(" - ")[0]
        start_cap = d["main"]["startingCapital"]
        trades = d["history"]["items"]
        equity = start_cap
        n_trades = 0
        for t in trades:
            net = t["net"]
            vol = t["volume"]
            cost = cost_per_oz * vol
            equity += (net - cost)
            n_trades += 1
        roi_pct = (equity - start_cap) / start_cap * 100
        rows.append({"month": month, "roi_pct": roi_pct, "totalTrades": n_trades})
    return pd.DataFrame(rows)


# ---------------------------------------------------------------------------
# Baseline: reuse the trade-level simulation, add cost per trade this time
# ---------------------------------------------------------------------------
def baseline_fold_roi(cost_per_oz):
    m5 = pd.read_csv("../cTrader/xauusd_m5_5y.sorted.csv")
    m5["timestamp"] = pd.to_datetime(m5["timestamp"], utc=True)
    m5 = m5.sort_values("timestamp").set_index("timestamp")
    h1 = m5.resample("1h", label="left", closed="left").agg(
        {"open": "first", "high": "max", "low": "min", "close": "last", "volume": "sum"}
    ).dropna().reset_index()

    close = h1["close"].values
    high = h1["high"].values
    low = h1["low"].values
    ts = h1["timestamp"]

    TREND_SMA, ATR_PERIOD = 200, 20
    SL_ATR, TP_ATR, TRAIL_ATR = 2.0, 5.0, 3.6
    RISK_PCT, MAX_LEV, LEV_BUFFER, START_BALANCE = 1.0, 10.0, 0.2, 10000.0

    trend_sma = pd.Series(close).rolling(TREND_SMA).mean().values
    trend_side = np.where(close > trend_sma, 1, np.where(close < trend_sma, -1, 0))
    prev_close = pd.Series(close).shift(1).values
    tr = np.maximum.reduce([high - low, np.abs(high - prev_close), np.abs(low - prev_close)])
    atr = np.full(len(tr), np.nan)
    atr[ATR_PERIOD] = np.nanmean(tr[1:ATR_PERIOD + 1])
    for i in range(ATR_PERIOD + 1, len(tr)):
        atr[i] = (atr[i - 1] * (ATR_PERIOD - 1) + tr[i]) / ATR_PERIOD
    warmup = max(TREND_SMA, ATR_PERIOD) + 5

    h1["month"] = ts.dt.to_period("M")
    rows = []
    for month in sorted(h1["month"].unique()):
        idxs = h1.index[h1["month"] == month].to_numpy()
        start_i, end_i = idxs[0], idxs[-1]
        if start_i < warmup:
            continue
        equity = START_BALANCE
        position = None
        trades = []
        for i in range(start_i, end_i + 1):
            if np.isnan(trend_sma[i]) or np.isnan(atr[i]) or atr[i] <= 0:
                continue
            if position is not None:
                d = position["direction"]
                exit_price = None
                hit_sl = (low[i] <= position["sl_price"]) if d == 1 else (high[i] >= position["sl_price"])
                hit_tp = (high[i] >= position["tp_price"]) if d == 1 else (low[i] <= position["tp_price"])
                if hit_sl and hit_tp:
                    exit_price = position["sl_price"]
                elif hit_sl:
                    exit_price = position["sl_price"]
                elif hit_tp:
                    exit_price = position["tp_price"]
                else:
                    atr_buf = atr[i] * TRAIL_ATR
                    if d == 1:
                        cand = close[i] - atr_buf
                        if cand > position["trail_level"]:
                            position["trail_level"] = cand
                        if close[i] < position["trail_level"]:
                            exit_price = close[i]
                    else:
                        cand = close[i] + atr_buf
                        if cand < position["trail_level"]:
                            position["trail_level"] = cand
                        if close[i] > position["trail_level"]:
                            exit_price = close[i]
                if exit_price is not None:
                    pnl = (exit_price - position["entry_price"]) * d * position["volume"]
                    cost = cost_per_oz * position["volume"]
                    equity += (pnl - cost)
                    trades.append(pnl - cost)
                    position = None
                else:
                    continue
            if position is None and trend_side[i] != 0:
                entry_price = close[i]
                stop_dist = atr[i] * SL_ATR
                tp_dist = atr[i] * TP_ATR
                d = trend_side[i]
                risk_dollars = equity * (RISK_PCT / 100.0)
                raw_vol = risk_dollars / stop_dist
                max_lev_vol = (equity * max(0, MAX_LEV - LEV_BUFFER)) / entry_price
                volume = min(raw_vol, max_lev_vol)
                if volume <= 0:
                    continue
                position = {
                    "direction": d, "entry_price": entry_price, "volume": volume,
                    "sl_price": entry_price - d * stop_dist, "tp_price": entry_price + d * tp_dist,
                    "trail_level": entry_price - d * atr[i] * TRAIL_ATR,
                }
        if position is not None:
            i = end_i
            pnl = (close[i] - position["entry_price"]) * position["direction"] * position["volume"]
            cost = cost_per_oz * position["volume"]
            equity += (pnl - cost)
            trades.append(pnl - cost)
        roi_pct = (equity - START_BALANCE) / START_BALANCE * 100
        rows.append({"month": str(month), "roi_pct": roi_pct, "totalTrades": len(trades)})
    return pd.DataFrame(rows)


print("Rebuilding baseline once (indicator/H1 computation is the slow part)...")
baseline_by_cost = {c: baseline_fold_roi(c) for c in COST_LEVELS}
print("Rebuilding bot folds from real trade history for each cost level...")
bot_by_cost = {c: bot_fold_roi(c) for c in COST_LEVELS}

print("\n=== Compounded return over overlapping months, by round-trip cost/oz ===")
print(f"{'cost/oz':>8} {'bot compounded':>16} {'bot avg trades/mo':>18} {'baseline compounded':>20} {'baseline avg trades/mo':>22}")
for c in COST_LEVELS:
    bot_df = bot_by_cost[c].copy()
    base_df = baseline_by_cost[c].copy()
    bot_df["month"] = pd.to_datetime(bot_df["month"], format="%d/%m/%Y").dt.to_period("M").astype(str)
    base_df["month"] = base_df["month"].astype(str)
    merged = bot_df.merge(base_df, on="month", suffixes=("_bot", "_base"))
    bot_comp = (1 + merged["roi_pct_bot"] / 100).prod()
    base_comp = (1 + merged["roi_pct_base"] / 100).prod()
    print(f"{c:>8.2f} {bot_comp:>16.3f}x {merged['totalTrades_bot'].mean():>17.1f} "
          f"{base_comp:>19.3f}x {merged['totalTrades_base'].mean():>21.1f}")

print(f"\n=== Detail at real cost=$0.48/oz round trip ===")
c = 0.48
bot_df = bot_by_cost[c].copy()
base_df = baseline_by_cost[c].copy()
bot_df["month"] = pd.to_datetime(bot_df["month"], format="%d/%m/%Y").dt.to_period("M").astype(str)
base_df["month"] = base_df["month"].astype(str)
merged = bot_df.merge(base_df, on="month", suffixes=("_bot", "_base"))
t, p = stats.ttest_rel(merged["roi_pct_bot"], merged["roi_pct_base"])
print(f"paired t-test monthly ROI% (bot vs baseline): t={t:.3f} p={p:.4f}")
print(f"bot: avg monthly ROI={merged['roi_pct_bot'].mean():+.3f}%  %%positive={100*(merged['roi_pct_bot']>0).mean():.1f}%")
print(f"baseline: avg monthly ROI={merged['roi_pct_base'].mean():+.3f}%  %%positive={100*(merged['roi_pct_base']>0).mean():.1f}%")

merged.to_csv("xau_sqz_v1_cost_adjusted_comparison.csv", index=False)
print("\nSaved: xau_sqz_v1_cost_adjusted_comparison.csv")
