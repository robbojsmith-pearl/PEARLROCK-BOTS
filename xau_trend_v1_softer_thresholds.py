#!/usr/bin/env python3
"""
Test the softer efficiency thresholds (0.05, 0.07) on the frozen 2017-2026
walk-forward, alongside 0.0 (unfiltered) and 0.14 (train winner) for
context — the train-only selection picked 0.14 mainly because it was the
only threshold to survive a uniformly-losing bear-market training window,
which is a noisy basis for picking a "winner." Report each candidate's
full test-period behavior, not just the technical winner.
"""

import numpy as np
import pandas as pd

M5_CSV = "../cTrader/xauusd_m5_2013_2026.csv"
ATR_PERIOD = 20
SL_ATR, TP_ATR, TRAIL_ATR = 2.0, 5.0, 3.6
RISK_PCT, MAX_LEV, LEV_BUFFER, START_BALANCE = 1.0, 10.0, 0.2, 10000.0
COST_PER_OZ = 0.48
TREND_PERIOD, EFF_WINDOW = 200, 200
THRESHOLDS = [0.0, 0.03, 0.05, 0.07, 0.10, 0.14]
TRAIN_END = "2016-12-31"

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
test_filter = lambda m: str(m) + "-01" > TRAIN_END


def run_backtest(eff_threshold):
    rows = []
    for month in [m for m in sorted(h1["month"].unique()) if test_filter(m)]:
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


windows = [
    ("2017-2019 chop", "2017-01", "2019-12"),
    ("2020 COVID vol", "2020-01", "2020-12"),
    ("2021-2022 pullback", "2021-01", "2022-12"),
    ("2023-2026 bull", "2023-01", "2026-12"),
]

print(f"{'threshold':>10} {'compounded':>11} {'avg_ROI/mo':>11} {'%pos':>6} {'trades/mo':>10} {'avgDD':>7}  |  regime compounded (chop / covid / pullback / bull)")
for thresh in THRESHOLDS:
    df = run_backtest(thresh)
    comp = (1 + df["roi_pct"] / 100).prod()
    regime_comps = []
    for label, start, end in windows:
        sub = df[(df["month"] >= start) & (df["month"] <= end)]
        regime_comps.append((1 + sub["roi_pct"] / 100).prod() if len(sub) else float("nan"))
    print(f"{thresh:>10.2f} {comp:>10.3f}x {df['roi_pct'].mean():>10.3f}% {100*(df['roi_pct']>0).mean():>5.1f}% "
          f"{df['totalTrades'].mean():>9.1f} {df['maxDD_pct'].mean():>6.2f}%  |  "
          + " / ".join(f"{c:.2f}x" for c in regime_comps))
    df.to_csv(f"xau_trend_v1_efffilter_t{thresh}_test_monthly.csv", index=False)
