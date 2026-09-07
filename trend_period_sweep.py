#!/usr/bin/env python3
"""
Sweep the trend-SMA lookback period to see whether 200H is actually a good
choice or just what XAU_SQZ_V1 happened to use.

Same methodology as the earlier direct forward-return test (large n,
statistically powered) rather than the slower monthly-fold P&L backtest:
for each candidate period N, direction = sign(price - SMA(N)) at every H1
bar, test the direction-aligned forward return at several horizons against
zero. Also reports trend-flip count (a trade-frequency / whipsaw proxy,
directly relevant given today's cost-sensitivity finding).

Explicit anti-overfitting framing: picking the single best N from a sweep
like this, with no out-of-sample split, IS a curve-fitting risk. The goal
here is to see whether there's a broad, stable region of good N values
(real structure) vs. an erratic spike at one lucky N (noise) — and to
report long/short separately since a chunk of any trend edge here could be
gold's 2021-2026 bull market, not a generic timeframe effect.
"""

import numpy as np
import pandas as pd
from scipy import stats

M5_CSV = "../cTrader/xauusd_m5_5y.sorted.csv"
PERIODS = [50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000]
FWD_HORIZONS = [5, 10, 20, 50]

m5 = pd.read_csv(M5_CSV)
m5["timestamp"] = pd.to_datetime(m5["timestamp"], utc=True)
m5 = m5.sort_values("timestamp").set_index("timestamp")
h1 = m5.resample("1h", label="left", closed="left").agg(
    {"open": "first", "high": "max", "low": "min", "close": "last", "volume": "sum"}
).dropna().reset_index()

close = h1["close"].values
log_close = np.log(close)
n = len(close)
print(f"H1 bars: {n}, {h1['timestamp'].iloc[0]} -> {h1['timestamp'].iloc[-1]}\n")

rows = []
for N in PERIODS:
    sma = pd.Series(close).rolling(N).mean().values
    side = np.where(close > sma, 1, np.where(close < sma, -1, 0))

    flips = np.sum(np.diff(side[~np.isnan(sma)]) != 0)
    valid_bars = np.sum(~np.isnan(sma))

    for h in FWD_HORIZONS:
        valid = np.arange(n - h)
        valid = valid[~np.isnan(sma[valid]) & (side[valid] != 0)]
        d = side[valid]
        fwd_ret = (log_close[valid + h] - log_close[valid]) * d * 100
        t, p = stats.ttest_1samp(fwd_ret, 0)

        long_mask = d == 1
        short_mask = d == -1
        long_ret = (log_close[valid[long_mask] + h] - log_close[valid[long_mask]]) * 100
        short_ret = -(log_close[valid[short_mask] + h] - log_close[valid[short_mask]]) * 100
        t_l, p_l = stats.ttest_1samp(long_ret, 0) if len(long_ret) > 10 else (np.nan, np.nan)
        t_s, p_s = stats.ttest_1samp(short_ret, 0) if len(short_ret) > 10 else (np.nan, np.nan)

        rows.append({
            "period_H": N, "horizon": h, "n": len(fwd_ret),
            "mean_%": fwd_ret.mean(), "t": t, "p": p,
            "long_mean_%": long_ret.mean(), "long_p": p_l,
            "short_mean_%": short_ret.mean(), "short_p": p_s,
            "flips_per_1000bars": flips / valid_bars * 1000,
        })

res = pd.DataFrame(rows)
pd.set_option("display.width", 220)
pd.set_option("display.max_columns", 20)

for h in FWD_HORIZONS:
    print(f"=== horizon={h}H ===")
    sub = res[res["horizon"] == h][["period_H", "n", "mean_%", "t", "p", "long_mean_%", "long_p",
                                     "short_mean_%", "short_p", "flips_per_1000bars"]]
    print(sub.round(4).to_string(index=False))
    print()

res.to_csv("trend_period_sweep.csv", index=False)
print("Saved: trend_period_sweep.csv")
