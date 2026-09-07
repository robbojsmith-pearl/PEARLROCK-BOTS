#!/usr/bin/env python3
"""
"No squeeze/momentum gate" baseline vs XAU_SQZ_V1's actual walk-forward.

Same risk management as the live bot (identical ATR-based SL/TP/trail,
identical risk-based sizing formula, identical fold structure: one
independent month at a time, $10,000 starting balance, chained via ROI%
the same way aggregate_fixed_walk.py does) — the ONLY difference is the
entry trigger: enter whenever flat and price is on one side of the 200H
trend SMA, with NO squeeze-release requirement and NO momentum
alignment/strengthening requirement, and NO momentum-fade soft exit
(since both pieces of "momentum machinery" are exactly what's being
tested for added value).

Execution-realism note: TP and SL are passed to cTrader as real broker-side
orders in the live bot (ExecuteMarketOrder(..., slPips, tpPips)), so they're
checked here against the bar's intrabar high/low. The trailing stop is
software-only (ManageOpenPosition calls ClosePosition manually, checked
against Bars.ClosePrices), so it's checked here against the bar CLOSE only
— matching the bot's actual code path, not an idealized version of it.

Known approximations (disclosed, not hidden):
  - No spread/commission modeled (the bot's real wf_results ARE cost-
    inclusive cTrader reports) — this comparison is gross-of-cost for the
    baseline, so its numbers will look somewhat better than a live
    equivalent would. The point is the RELATIVE structure (does the extra
    entry gating help), not an absolute cost-matched comparison.
  - Entry price approximated as bar close, not next-bar-open or live ask/bid.
  - Position size ignores broker min/max/step lot rounding.
  - Same-bar SL+TP ambiguity (can't tell which triggered first from an H1
    OHLC bar) resolved conservatively as "SL first" — counted and reported.
"""

import numpy as np
import pandas as pd

M5_CSV = "../cTrader/xauusd_m5_5y.sorted.csv"
WF_CSV = "wf_results/aggregate_summary.csv"

# Live fixed params (fixed_param_monthly_walk.sh)
TREND_SMA = 200
ATR_PERIOD = 20
SL_ATR, TP_ATR, TRAIL_ATR = 2.0, 5.0, 3.6
RISK_PCT = 1.0
MAX_LEV, LEV_BUFFER = 10.0, 0.2
START_BALANCE = 10000.0

# ---------------------------------------------------------------------------
# Build H1 bars + indicators
# ---------------------------------------------------------------------------
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

trend_sma = pd.Series(close).rolling(TREND_SMA).mean().values
trend_side = np.where(close > trend_sma, 1, np.where(close < trend_sma, -1, 0))

prev_close = pd.Series(close).shift(1).values
tr = np.maximum.reduce([
    high - low,
    np.abs(high - prev_close),
    np.abs(low - prev_close),
])
# Wilder-smoothed ATR (matches cAlgo's MovingAverageType.WilderSmoothing used
# for the bot's own _atr — RMA/EMA-style, not a plain SMA of TR)
atr = np.full(len(tr), np.nan)
first_valid = ATR_PERIOD
atr[first_valid] = np.nanmean(tr[1:first_valid + 1])
for i in range(first_valid + 1, len(tr)):
    atr[i] = (atr[i - 1] * (ATR_PERIOD - 1) + tr[i]) / ATR_PERIOD

warmup = max(TREND_SMA, ATR_PERIOD) + 5

# ---------------------------------------------------------------------------
# Monthly fold simulation
# ---------------------------------------------------------------------------
h1["month"] = ts.dt.to_period("M")
months = sorted(h1["month"].unique())
n = len(h1)

fold_rows = []

for month in months:
    idxs = h1.index[h1["month"] == month].to_numpy()
    if len(idxs) == 0:
        continue
    start_i, end_i = idxs[0], idxs[-1]
    if start_i < warmup:
        continue  # not enough trend-SMA history yet

    equity = START_BALANCE
    peak_equity = START_BALANCE
    max_dd_pct = 0.0
    position = None  # dict: direction, entry_price, entry_atr, tp_price, sl_price, trail_level, volume
    trades = []
    ambiguous_same_bar = 0

    for i in range(start_i, end_i + 1):
        if np.isnan(trend_sma[i]) or np.isnan(atr[i]) or atr[i] <= 0:
            continue

        if position is not None:
            d = position["direction"]
            exit_price = None
            reason = None

            hit_sl = (low[i] <= position["sl_price"]) if d == 1 else (high[i] >= position["sl_price"])
            hit_tp = (high[i] >= position["tp_price"]) if d == 1 else (low[i] <= position["tp_price"])

            if hit_sl and hit_tp:
                ambiguous_same_bar += 1
                exit_price, reason = position["sl_price"], "sl_ambiguous_conservative"
            elif hit_sl:
                exit_price, reason = position["sl_price"], "sl"
            elif hit_tp:
                exit_price, reason = position["tp_price"], "tp"
            else:
                # trailing stop, software-only, checked against bar CLOSE
                atr_buf = atr[i] * TRAIL_ATR
                if d == 1:
                    cand = close[i] - atr_buf
                    if cand > position["trail_level"]:
                        position["trail_level"] = cand
                    if close[i] < position["trail_level"]:
                        exit_price, reason = close[i], "trail"
                else:
                    cand = close[i] + atr_buf
                    if cand < position["trail_level"]:
                        position["trail_level"] = cand
                    if close[i] > position["trail_level"]:
                        exit_price, reason = close[i], "trail"

            if exit_price is not None:
                pnl = (exit_price - position["entry_price"]) * d * position["volume"]
                equity += pnl
                peak_equity = max(peak_equity, equity)
                dd = (peak_equity - equity) / peak_equity * 100 if peak_equity > 0 else 0
                max_dd_pct = max(max_dd_pct, dd)
                trades.append(pnl)
                position = None
            else:
                continue  # still open, no entry check this bar

        if position is None and trend_side[i] != 0:
            entry_price = close[i]
            stop_dist = atr[i] * SL_ATR
            tp_dist = atr[i] * TP_ATR
            d = trend_side[i]

            risk_dollars = equity * (RISK_PCT / 100.0)
            raw_vol = risk_dollars / stop_dist
            effective_lev = max(0, MAX_LEV - LEV_BUFFER)
            max_lev_vol = (equity * effective_lev) / entry_price
            volume = min(raw_vol, max_lev_vol)
            if volume <= 0:
                continue

            position = {
                "direction": d, "entry_price": entry_price, "volume": volume,
                "sl_price": entry_price - d * stop_dist,
                "tp_price": entry_price + d * tp_dist,
                "trail_level": entry_price - d * atr[i] * TRAIL_ATR,
            }

    # mark-to-market any position still open at fold end (same convention as wf_results)
    if position is not None:
        i = end_i
        mtm_pnl = (close[i] - position["entry_price"]) * position["direction"] * position["volume"]
        equity += mtm_pnl
        peak_equity = max(peak_equity, equity)
        dd = (peak_equity - equity) / peak_equity * 100 if peak_equity > 0 else 0
        max_dd_pct = max(max_dd_pct, dd)
        trades.append(mtm_pnl)

    net_profit = equity - START_BALANCE
    roi_pct = net_profit / START_BALANCE * 100
    wins = [t for t in trades if t > 0]
    losses = [t for t in trades if t <= 0]
    gross_win = sum(wins)
    gross_loss = -sum(losses)
    pf = (gross_win / gross_loss) if gross_loss > 0 else (np.inf if gross_win > 0 else 0)

    fold_rows.append({
        "month": str(month), "roi_pct": roi_pct, "netProfit": net_profit,
        "profitFactor": pf, "totalTrades": len(trades), "winningTrades": len(wins),
        "losingTrades": len(losses), "avgTrade": net_profit / len(trades) if trades else 0,
        "maxEquityDDPct": max_dd_pct, "ambiguous_same_bar": ambiguous_same_bar,
    })

baseline = pd.DataFrame(fold_rows)
baseline["compounded_index"] = (1 + baseline["roi_pct"] / 100).cumprod()
baseline.to_csv("xau_sqz_v1_baseline_notimingfilter_monthly.csv", index=False)

print(f"Baseline folds simulated: {len(baseline)}  ({baseline['month'].iloc[0]} -> {baseline['month'].iloc[-1]})")
print(f"Total ambiguous same-bar SL/TP conflicts (resolved conservatively as SL): "
      f"{baseline['ambiguous_same_bar'].sum()} across all folds")

# ---------------------------------------------------------------------------
# Compare against the bot's real walk-forward, on overlapping months only
# ---------------------------------------------------------------------------
wf = pd.read_csv(WF_CSV)
wf["month"] = pd.to_datetime(wf["month"]).dt.to_period("M").astype(str)
baseline["month"] = baseline["month"].astype(str)

merged = wf.merge(baseline, on="month", suffixes=("_bot", "_baseline"))
print(f"\nOverlapping months for comparison: {len(merged)}")

print("\n=== Headline comparison (overlapping months only) ===")
for label, df, roi_col, pf_col, trades_col, dd_col in [
    ("XAU_SQZ_V1 (squeeze+trend+momentum, real cTrader WF)", merged, "roi_pct_bot", "profitFactor_bot", "totalTrades_bot", "maxEquityDDPct_bot"),
    ("Baseline (trend-only, no squeeze/momentum gate)", merged, "roi_pct_baseline", "profitFactor_baseline", "totalTrades_baseline", "maxEquityDDPct_baseline"),
]:
    total_trades = df[trades_col].sum()
    avg_roi = df[roi_col].mean()
    win_months = (df[roi_col] > 0).mean() * 100
    compounded = (1 + df[roi_col] / 100).prod()
    avg_dd = df[dd_col].mean()
    print(f"{label}")
    print(f"  total trades={total_trades:.0f}  avg monthly ROI={avg_roi:+.2f}%  "
          f"%%positive months={win_months:.1f}%  compounded over period={compounded:.3f}x  avg monthly maxDD={avg_dd:.2f}%")

from scipy import stats
t, p = stats.ttest_rel(merged["roi_pct_bot"], merged["roi_pct_baseline"])
print(f"\nPaired t-test, monthly ROI% (bot vs baseline), n={len(merged)}: t={t:.3f} p={p:.4f}")

corr, corr_p = stats.pearsonr(merged["roi_pct_bot"], merged["roi_pct_baseline"])
print(f"Correlation of monthly ROI% between bot and baseline: r={corr:.3f} (p={corr_p:.4f})")

print("\n=== Month-by-month (last 15) ===")
cols = ["month", "roi_pct_bot", "totalTrades_bot", "roi_pct_baseline", "totalTrades_baseline"]
print(merged[cols].tail(15).round(2).to_string(index=False))

print("\nSaved: xau_sqz_v1_baseline_notimingfilter_monthly.csv")
