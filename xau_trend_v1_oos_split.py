#!/usr/bin/env python3
"""
80/20 chronological train/test split for XAU_TREND_V1.

Protocol (the point of doing this at all): re-run the {period, buffer} grid
selection using ONLY the first 80% of months, pick the winner from THAT
data alone, then evaluate — for the first time — that exact combo on the
untouched last 20%. Also show the full grid's test-period ranking, not
just the chosen winner, so a train/test ranking flip (the real overfitting
tell) is visible rather than hidden. XAU_SQZ_V1's own real numbers get the
same split for context.
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
TRAIN_FRAC = 0.8

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
            trades.append(pnl - cost)
        roi_pct = (equity - START_BALANCE) / START_BALANCE * 100
        rows.append({"month": str(month), "roi_pct": roi_pct, "totalTrades": len(trades)})
    return pd.DataFrame(rows)


print("Running grid across all months...")
grid = {(p, b): run_backtest(p, b) for p in PERIODS for b in BUFFERS}

# align to the same 59-month set used for the bot comparison, for a clean split
bot = pd.read_csv("xau_sqz_v1_cost_adjusted_comparison.csv")
all_months = sorted(bot["month"].tolist())
n_train = int(len(all_months) * TRAIN_FRAC)
train_months, test_months = set(all_months[:n_train]), set(all_months[n_train:])
print(f"\nTotal months: {len(all_months)}  train={len(train_months)} ({all_months[0]}..{all_months[n_train-1]})  "
      f"test={len(test_months)} ({all_months[n_train]}..{all_months[-1]})")


def compounded(df, months):
    sub = df[df["month"].isin(months)]
    return (1 + sub["roi_pct"] / 100).prod(), sub["roi_pct"].mean(), sub["totalTrades"].mean(), len(sub)


print("\n=== Grid: TRAIN performance (selection happens here only) ===")
train_scores = {}
for key, df in grid.items():
    comp, avg_roi, avg_trades, n = compounded(df, train_months)
    train_scores[key] = comp
    print(f"period={key[0]:4d}H buffer={key[1]:.1f}  train_compounded={comp:.3f}x  "
          f"avg_ROI/mo={avg_roi:+.3f}%  n={n}")

winner = max(train_scores, key=train_scores.get)
print(f"\nWinner selected on TRAIN alone: period={winner[0]}H buffer={winner[1]}xATR "
      f"(train compounded {train_scores[winner]:.3f}x)")

print("\n=== Grid: TEST performance (never touched during selection) — full ranking, not just the winner ===")
test_scores = {}
for key, df in grid.items():
    comp, avg_roi, avg_trades, n = compounded(df, test_months)
    test_scores[key] = comp
    tag = "  <-- TRAIN WINNER" if key == winner else ""
    print(f"period={key[0]:4d}H buffer={key[1]:.1f}  test_compounded={comp:.3f}x  "
          f"avg_ROI/mo={avg_roi:+.3f}%  n={n}{tag}")

test_rank = sorted(test_scores, key=test_scores.get, reverse=True)
print(f"\nTrain winner's rank on test set: {test_rank.index(winner)+1} of {len(test_rank)} "
      f"(1=best) — best on test was period={test_rank[0][0]}H buffer={test_rank[0][1]}")

# ---------------------------------------------------------------------------
# Bot's own real numbers, same split, for context
# ---------------------------------------------------------------------------
bot_train = bot[bot["month"].isin(train_months)]
bot_test = bot[bot["month"].isin(test_months)]
bot_train_comp = (1 + bot_train["roi_pct_bot"] / 100).prod()
bot_test_comp = (1 + bot_test["roi_pct_bot"] / 100).prod()

winner_df = grid[winner]
winner_test = winner_df[winner_df["month"].isin(test_months)]
bot_test_aligned = bot_test.merge(winner_test, on="month", suffixes=("_bot", "_trend"))
t, p = stats.ttest_rel(bot_test_aligned["roi_pct_bot"], bot_test_aligned["roi_pct"])

print(f"\n=== TEST-period comparison: train-selected XAU_TREND_V1 winner vs XAU_SQZ_V1 (real) ===")
print(f"XAU_SQZ_V1 test compounded: {bot_test_comp:.3f}x  (train was {bot_train_comp:.3f}x)")
wc, wroi, wtr, wn = compounded(winner_df, test_months)
print(f"XAU_TREND_V1 (period={winner[0]}H buf={winner[1]}) test compounded: {wc:.3f}x  "
      f"(train was {train_scores[winner]:.3f}x)")
print(f"Paired t-test on test-period monthly ROI%, n={len(bot_test_aligned)}: t={t:.3f} p={p:.4f}")

grid[winner].to_csv("xau_trend_v1_oos_winner_monthly.csv", index=False)
print("\nSaved: xau_trend_v1_oos_winner_monthly.csv")
