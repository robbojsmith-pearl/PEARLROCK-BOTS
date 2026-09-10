#!/usr/bin/env python3
"""
XAU_TREND_V1 with continuous efficiency-based sizing instead of a hard gate.

Entry stays exactly as the unfiltered version (price > SMA(200), no
efficiency cutoff) — every trend-aligned bar can still trigger. What
changes is position size: instead of a flat 1% risk, size is scaled by
how trending the market currently is, using the SAME efficiency signal as
the hard-filter test:

  factor = clip(efficiency / REF_EFF, MIN_FACTOR, MAX_FACTOR)
  volume = base_risk_volume * factor   (still capped by the leverage limit)

REF_EFF = median bar-level efficiency over the TRAIN window only
(2013-2016) — a natural normalization anchor, not a tuned/swept parameter,
so this isn't reintroducing the same multiple-testing problem the hard
threshold sweep had. MIN/MAX_FACTOR are fixed, round, sane bounds (0.25x
to 2.0x) rather than grid-searched.

Idea: a trade taken during a strong, clean trend gets sized up; a trade
taken during choppy conditions still gets taken (keeps some bull-market
participation the hard filter gave up) but sized down (reduces the
2021-2022-style damage without fully sitting out).
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
TREND_PERIOD, EFF_WINDOW = 200, 200
MIN_FACTOR, MAX_FACTOR = 0.25, 2.0
TRAIN_START, TRAIN_END = "2013-01-01", "2016-12-31"

print("Loading full history...")
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
print(f"REF_EFF (train-only median efficiency): {REF_EFF:.4f}")


def run_backtest(months_filter=None, use_sizing_factor=True):
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
            if position is None and close[i] > sma[i]:
                entry_price = close[i]
                stop_dist = atr[i] * SL_ATR
                tp_dist = atr[i] * TP_ATR
                risk_dollars = equity * (RISK_PCT / 100.0)
                raw_vol = risk_dollars / stop_dist
                if use_sizing_factor:
                    factor = np.clip(efficiency[i] / REF_EFF, MIN_FACTOR, MAX_FACTOR)
                    raw_vol *= factor
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


test_filter = lambda m: str(m) + "-01" > TRAIN_END
print("\nRunning continuous-sizing backtest, 2017-2026 (frozen design, nothing tuned on test)...")
sized = run_backtest(months_filter=test_filter, use_sizing_factor=True)
sized.to_csv("xau_trend_v1_contsizing_test_monthly.csv", index=False)

unfiltered = pd.read_csv("xau_trend_v1_fullhistory_test_monthly.csv")

comp_s = (1 + sized["roi_pct"] / 100).prod()
comp_u = (1 + unfiltered["roi_pct"] / 100).prod()
print(f"\nContinuous sizing: compounded={comp_s:.3f}x  avg_ROI/mo={sized['roi_pct'].mean():+.3f}%  "
      f"%%pos={100*(sized['roi_pct']>0).mean():.1f}%  avg_trades/mo={sized['totalTrades'].mean():.1f}  "
      f"avg_maxDD={sized['maxDD_pct'].mean():.2f}%")
print(f"Unfiltered (flat 1% risk): compounded={comp_u:.3f}x  avg_ROI/mo={unfiltered['roi_pct'].mean():+.3f}%  "
      f"%%pos={100*(unfiltered['roi_pct']>0).mean():.1f}%  avg_trades/mo={unfiltered['totalTrades'].mean():.1f}  "
      f"avg_maxDD={unfiltered['maxDD_pct'].mean():.2f}%")

merged = unfiltered.merge(sized, on="month", suffixes=("_unfiltered", "_sized"))
t, p = stats.ttest_rel(merged["roi_pct_sized"], merged["roi_pct_unfiltered"])
print(f"\nPaired t-test, sized vs unfiltered, n={len(merged)}: t={t:.3f} p={p:.4f}")

print("\n=== By regime sub-window ===")
windows = [
    ("2017-2019 chop", "2017-01", "2019-12"),
    ("2020 COVID vol", "2020-01", "2020-12"),
    ("2021-2022 pullback", "2021-01", "2022-12"),
    ("2023-2026 bull", "2023-01", "2026-12"),
]
for label, start, end in windows:
    sub_s = sized[(sized["month"] >= start) & (sized["month"] <= end)]
    sub_u = unfiltered[(unfiltered["month"] >= start) & (unfiltered["month"] <= end)]
    cs = (1 + sub_s["roi_pct"] / 100).prod()
    cu = (1 + sub_u["roi_pct"] / 100).prod()
    print(f"{label:22s} n={len(sub_s):3d}  sized: compounded={cs:.3f}x avg_ROI/mo={sub_s['roi_pct'].mean():+.3f}% avg_maxDD={sub_s['maxDD_pct'].mean():.2f}%  |  "
          f"unfiltered: compounded={cu:.3f}x avg_ROI/mo={sub_u['roi_pct'].mean():+.3f}% avg_maxDD={sub_u['maxDD_pct'].mean():.2f}%")

print("\n=== vs XAU_SQZ_V1 real trades ===")
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
merged_bot = bot_df.merge(sized, on="month", suffixes=("_bot", "_sized"))
t2, p2 = stats.ttest_rel(merged_bot["roi_pct_bot"], merged_bot["roi_pct_sized"])
print(f"n={len(merged_bot)}  bot compounded={(1+merged_bot['roi_pct_bot']/100).prod():.3f}x  "
      f"sized_trend compounded={(1+merged_bot['roi_pct_sized']/100).prod():.3f}x  paired t={t2:.3f} p={p2:.4f}")

print("\nSaved: xau_trend_v1_contsizing_test_monthly.csv")
