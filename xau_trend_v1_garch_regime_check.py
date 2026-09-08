#!/usr/bin/env python3
"""
Does GARCH-forecast volatility actually separate XAU_TREND_V1's good months
from its bad ones? Diagnostic before building anything — GARCH forecasts
magnitude of moves, not directional persistence, so there's a real question
whether it's even the right tool for "regime change" in a trend-following
context (the 2021-2022 losing stretch wasn't necessarily LOW vol, it was
directionless — a different axis).

Rebuilds the walk-forward GJR-GARCH daily vol forecast on the FULL
2013-2026 history (much more regime-diverse than the 5-year fit used
earlier today), then checks it against XAU_TREND_V1's actual monthly
walk-forward results two ways:
  1. Correlation of monthly avg forecast vol vs monthly ROI, full 117-month
     test period.
  2. Regime-by-regime average forecast vol, to see whether the 2021-2022
     losing window actually looks different on vol alone.
Also builds a simple TREND-PERSISTENCE proxy (rolling correlation of price
with time / Hurst-style efficiency) as the natural complement, and checks
whether THAT separates the regimes better than vol does.
"""

import numpy as np
import pandas as pd
from arch import arch_model
from scipy import stats

M5_CSV = "../cTrader/xauusd_m5_2013_2026.csv"
TREND_MONTHLY = "xau_trend_v1_fullhistory_test_monthly.csv"
BURN_IN_DAYS = 500
REFIT_EVERY_DAYS = 21

# ---------------------------------------------------------------------------
# 1. Walk-forward GJR-GARCH on full daily history (reuses today's earlier method)
# ---------------------------------------------------------------------------
print("Loading full M5 history and building daily returns...")
m5 = pd.read_csv(M5_CSV)
m5["timestamp"] = pd.to_datetime(m5["timestamp"], utc=True)
m5["date"] = m5["timestamp"].dt.date
daily_close = m5.groupby("date")["close"].last()
daily_close.index = pd.to_datetime(daily_close.index)
daily_ret = np.log(daily_close / daily_close.shift(1)).dropna()
dates = daily_ret.index
n_days = len(daily_ret)
print(f"Daily returns: {n_days} days, {dates[0].date()} -> {dates[-1].date()}")
ret_pct = daily_ret.values * 100


def fit_gjr(ret_window):
    am = arch_model(ret_window, vol="Garch", p=1, o=1, q=1, dist="t", mean="Constant")
    return am.fit(disp="off")


print("Fitting walk-forward GJR-GARCH (this is the slow part)...")
forecast_var = np.full(n_days, np.nan)
sigma2 = None
params = None
for t in range(BURN_IN_DAYS, n_days):
    if params is None or (t - BURN_IN_DAYS) % REFIT_EVERY_DAYS == 0:
        res = fit_gjr(ret_pct[:t])
        p = res.params
        params = (p["omega"], p["alpha[1]"], p["beta[1]"], p["gamma[1]"])
        sigma2 = res.conditional_volatility[-1] ** 2
    omega, alpha, beta, gamma = params
    eps_prev = ret_pct[t - 1]
    ind = 1.0 if eps_prev < 0 else 0.0
    sigma2 = omega + alpha * eps_prev**2 + gamma * ind * eps_prev**2 + beta * sigma2
    forecast_var[t] = sigma2

fvol = pd.DataFrame({"date": dates, "gjr_fvol": np.sqrt(forecast_var) / 100}).dropna()
fvol["month"] = fvol["date"].values.astype("datetime64[M]")
monthly_vol = fvol.groupby("month")["gjr_fvol"].mean().reset_index()
monthly_vol.columns = ["month", "avg_garch_fvol"]
monthly_vol["month"] = monthly_vol["month"].astype(str).str[:7]

# ---------------------------------------------------------------------------
# 2. Trend-persistence proxy: rolling efficiency ratio (Kaufman-style) —
#    net directional move / total path length over a trailing window.
#    ~1 = pure trend, ~0 = pure chop/noise, regardless of volatility level.
# ---------------------------------------------------------------------------
h1 = m5.set_index("timestamp").resample("1h", label="left", closed="left").agg(
    {"open": "first", "high": "max", "low": "min", "close": "last"}
).dropna().reset_index()
close = h1["close"].values
WINDOW = 200  # same horizon as the trend SMA, for direct comparability
net_move = np.abs(pd.Series(close).diff(WINDOW))
path_len = pd.Series(close).diff().abs().rolling(WINDOW).sum()
efficiency = (net_move / path_len).values
h1["efficiency"] = efficiency
h1["month"] = h1["timestamp"].dt.to_period("M").astype(str)
monthly_eff = h1.groupby("month")["efficiency"].mean().reset_index()
monthly_eff.columns = ["month", "avg_efficiency"]

# ---------------------------------------------------------------------------
# 3. Merge with XAU_TREND_V1's actual monthly results and test both signals
# ---------------------------------------------------------------------------
trend = pd.read_csv(TREND_MONTHLY)
merged = trend.merge(monthly_vol, on="month", how="inner").merge(monthly_eff, on="month", how="inner")
merged.to_csv("xau_trend_v1_garch_regime_merged.csv", index=False)
print(f"\nMerged months: {len(merged)}")

print("\n=== Correlation with monthly ROI% ===")
for col in ["avg_garch_fvol", "avg_efficiency"]:
    r, p = stats.pearsonr(merged[col], merged["roi_pct"])
    sr, sp = stats.spearmanr(merged[col], merged["roi_pct"])
    print(f"{col:18s}  Pearson r={r:+.3f} (p={p:.4f})   Spearman rho={sr:+.3f} (p={sp:.4f})")

print("\n=== By regime window: avg GARCH-forecast vol vs avg trend-efficiency ===")
windows = [
    ("2017-2019 chop", "2017-01", "2019-12"),
    ("2020 COVID vol", "2020-01", "2020-12"),
    ("2021-2022 pullback (the loser)", "2021-01", "2022-12"),
    ("2023-2026 bull", "2023-01", "2026-12"),
]
for label, start, end in windows:
    sub = merged[(merged["month"] >= start) & (merged["month"] <= end)]
    if len(sub) == 0:
        continue
    print(f"{label:32s} n={len(sub):3d}  avg_ROI/mo={sub['roi_pct'].mean():+.3f}%  "
          f"avg_garch_fvol={sub['avg_garch_fvol'].mean():.5f}  avg_efficiency={sub['avg_efficiency'].mean():.4f}")

print("\n=== Tercile split by each signal: does either separate winning from losing months? ===")
for col in ["avg_garch_fvol", "avg_efficiency"]:
    merged[f"{col}_tercile"] = pd.qcut(merged[col], 3, labels=["low", "mid", "high"])
    g = merged.groupby(f"{col}_tercile", observed=True)["roi_pct"].agg(["mean", "count"])
    print(f"\n{col}:")
    print(g.round(4).to_string())

print("\nSaved: xau_trend_v1_garch_regime_merged.csv")
