#!/usr/bin/env python3
"""
Statistical test of XAU_SQZ_V1's entry signal on H1 XAUUSD, using the same
rigor as the M5 analysis (ACF, Hurst, Ljung-Box) — this time asking a
sharper question: does the bot's specific squeeze-release + trend + momentum
signal predict forward direction, not just "is H1 a random walk in general."

Replicates the live bot's exact logic (BB/KC squeeze, 200H trend SMA, 20-bar
LR momentum projection, entry gating) in Python against H1 bars built by
resampling the M5 dataset, using the bot's actual live parameters:
  BBPeriod=24 BBStdDev=1.5 KCPeriod=24 KCMultiplier=1.9 TrendSmaPeriod=200
  MomentumWindow=20 MomentumFadeRatio=0.7 (fade ratio not needed for entry test)

Two questions:
  1. Unconditional H1: does raw H1 have any more linear structure than M5 did?
  2. Conditional on the bot's actual entry signal: is the forward, direction-
     aligned return significantly different from zero at 1/5/10/20/50 bars out?
"""

import numpy as np
import pandas as pd
from scipy import stats
from statsmodels.tsa.stattools import adfuller, acf
from statsmodels.stats.diagnostic import acorr_ljungbox

M5_CSV = "../cTrader/xauusd_m5_5y.sorted.csv"

BB_PERIOD, BB_STDDEV = 24, 1.5
KC_PERIOD, KC_MULT = 24, 1.9
TREND_SMA = 200
MOM_WINDOW = 20
FWD_HORIZONS = [1, 5, 10, 20, 50]

# ---------------------------------------------------------------------------
# Build H1 bars from M5
# ---------------------------------------------------------------------------
m5 = pd.read_csv(M5_CSV)
m5["timestamp"] = pd.to_datetime(m5["timestamp"], utc=True)
m5 = m5.sort_values("timestamp").set_index("timestamp")

h1 = m5.resample("1h", label="left", closed="left").agg({
    "open": "first", "high": "max", "low": "min", "close": "last", "volume": "sum"
}).dropna()
h1 = h1.reset_index()
print(f"H1 bars built: {len(h1)}, {h1['timestamp'].iloc[0]} -> {h1['timestamp'].iloc[-1]}")

# ---------------------------------------------------------------------------
# Replicate bot indicators
# ---------------------------------------------------------------------------
close = h1["close"]
high = h1["high"]
low = h1["low"]

bb_mid = close.rolling(BB_PERIOD).mean()
bb_std = close.rolling(BB_PERIOD).std()
bb_upper = bb_mid + BB_STDDEV * bb_std
bb_lower = bb_mid - BB_STDDEV * bb_std

prev_close = close.shift(1)
tr = pd.concat([high - low, (high - prev_close).abs(), (low - prev_close).abs()], axis=1).max(axis=1)
kc_atr = tr.rolling(KC_PERIOD).mean()
kc_mid = close.rolling(KC_PERIOD).mean()
kc_upper = kc_mid + KC_MULT * kc_atr
kc_lower = kc_mid - KC_MULT * kc_atr

squeeze_on = (bb_lower > kc_lower) & (bb_upper < kc_upper)
just_released = squeeze_on.shift(1).fillna(False) & (~squeeze_on)

trend_sma = close.rolling(TREND_SMA).mean()
trend_side = np.where(close > trend_sma, 1, np.where(close < trend_sma, -1, 0))

hh20 = high.rolling(MOM_WINDOW).max()
ll20 = low.rolling(MOM_WINDOW).min()
sma20 = close.rolling(MOM_WINDOW).mean()
midline = ((hh20 + ll20) / 2.0 + sma20) / 2.0
mom_source = close - midline


def rolling_lr_projection(arr):
    n = len(arr)
    x = np.arange(n)
    denom = n * np.sum(x * x) - np.sum(x) ** 2
    slope = (n * np.sum(x * arr) - np.sum(x) * np.sum(arr)) / denom
    intercept = (np.sum(arr) - slope * np.sum(x)) / n
    return slope * (n - 1) + intercept


momentum = mom_source.rolling(MOM_WINDOW).apply(rolling_lr_projection, raw=True)
momentum_prev = momentum.shift(1)

# ---------------------------------------------------------------------------
# Entry signal, exactly matching EntrySignal.Evaluate
# ---------------------------------------------------------------------------
aligned_curr = momentum * trend_side
aligned_prev = momentum_prev * trend_side
should_enter = (
    just_released
    & (trend_side != 0)
    & (aligned_curr > 0)
    & (aligned_curr > aligned_prev)
)
direction = np.where(should_enter, trend_side, 0)

h1["squeeze_on"] = squeeze_on
h1["just_released"] = just_released
h1["should_enter"] = should_enter
h1["direction"] = direction

n_signals = should_enter.sum()
n_releases = just_released.sum()
print(f"Squeeze releases: {n_releases}  |  Full entry signals (release+trend+momentum aligned): {n_signals}")

# ---------------------------------------------------------------------------
# 1. Unconditional H1 structure (mirrors the M5 pass)
# ---------------------------------------------------------------------------
h1_ret = np.log(close / close.shift(1)).dropna()
print(f"\n=== 1. Unconditional H1 log-return structure (n={len(h1_ret)}) ===")
print(f"mean={h1_ret.mean():.6e}  std={h1_ret.std():.6e}  skew={stats.skew(h1_ret):.3f}  "
      f"excess_kurt={stats.kurtosis(h1_ret):.3f}")

adf_ret = adfuller(h1_ret.values, maxlag=20, autolag="AIC")
print(f"ADF on H1 log-returns: stat={adf_ret[0]:.3f} p={adf_ret[1]:.2e} (expect stationary)")

lb = acorr_ljungbox(h1_ret, lags=[1, 5, 10, 20], return_df=True)
print("Ljung-Box on H1 returns (linear autocorrelation):")
print(lb.to_string())
lb_sq = acorr_ljungbox(h1_ret**2, lags=[1, 5, 10, 20], return_df=True)
print("Ljung-Box on H1 squared returns (vol clustering):")
print(lb_sq.to_string())

acf_vals = acf(h1_ret, nlags=10, fft=True)
print("ACF of H1 returns, lags 1-10:", np.round(acf_vals[1:], 4))


def hurst_exponent(series, max_lag=100):
    lags = range(2, max_lag)
    tau = [np.std(series[lag:] - series[:-lag]) for lag in lags]
    poly = np.polyfit(np.log(list(lags)), np.log(tau), 1)
    return poly[0]


cum = h1_ret.cumsum().values
print(f"Hurst exponent (H1, trading-time cumulative returns, lags 2-100): {hurst_exponent(cum, 100):.3f}")
print(f"Hurst exponent (H1, lags 2-20, ~2h-20h horizon): {hurst_exponent(cum, 20):.3f}")

# ---------------------------------------------------------------------------
# 2. Conditional: forward returns following the bot's actual entry signal
# ---------------------------------------------------------------------------
print(f"\n=== 2. Forward direction-aligned returns following FULL entry signal (n={n_signals}) ===")
log_close = np.log(close.values)
signal_idx = np.where(should_enter.values)[0]
dir_at_signal = direction[signal_idx]

print(f"{'horizon':>8} {'n':>6} {'mean_%':>10} {'median_%':>10} {'std_%':>8} {'t':>8} {'p':>10}")
for h in FWD_HORIZONS:
    valid = signal_idx[signal_idx + h < len(log_close)]
    d = dir_at_signal[:len(valid)]
    fwd_ret = (log_close[valid + h] - log_close[valid]) * d * 100
    t, p = stats.ttest_1samp(fwd_ret, 0)
    print(f"{h:>8} {len(fwd_ret):>6} {fwd_ret.mean():>10.4f} {np.median(fwd_ret):>10.4f} "
          f"{fwd_ret.std():>8.4f} {t:>8.3f} {p:>10.4f}")

print(f"\n=== Same test, squeeze-release only (no trend/momentum gate, n={n_releases}) — is the extra gating pulling its weight? ===")
release_idx = np.where(just_released.values)[0]
# no direction at bare release — use trend_side at that bar as a naive direction proxy
trend_at_release = trend_side[release_idx]
print(f"{'horizon':>8} {'n':>6} {'mean_%':>10} {'median_%':>10} {'std_%':>8} {'t':>8} {'p':>10}")
for h in FWD_HORIZONS:
    valid_mask = release_idx + h < len(log_close)
    valid = release_idx[valid_mask]
    d = trend_at_release[valid_mask]
    fwd_ret = (log_close[valid + h] - log_close[valid]) * d * 100
    t, p = stats.ttest_1samp(fwd_ret, 0)
    print(f"{h:>8} {len(fwd_ret):>6} {fwd_ret.mean():>10.4f} {np.median(fwd_ret):>10.4f} "
          f"{fwd_ret.std():>8.4f} {t:>8.3f} {p:>10.4f}")

print(f"\n=== Baseline: forward returns from ALL bars, direction = sign of trend at that bar (n={len(log_close)}) ===")
all_dir = trend_side
print(f"{'horizon':>8} {'n':>6} {'mean_%':>10} {'median_%':>10} {'std_%':>8} {'t':>8} {'p':>10}")
for h in FWD_HORIZONS:
    valid = np.arange(len(log_close) - h)
    valid = valid[all_dir[valid] != 0]
    d = all_dir[valid]
    fwd_ret = (log_close[valid + h] - log_close[valid]) * d * 100
    t, p = stats.ttest_1samp(fwd_ret, 0)
    print(f"{h:>8} {len(fwd_ret):>6} {fwd_ret.mean():>10.4f} {np.median(fwd_ret):>10.4f} "
          f"{fwd_ret.std():>8.4f} {t:>8.3f} {p:>10.4f}")

h1.to_csv("xau_sqz_v1_h1_signal_test.csv", index=False)
print("\nSaved: xau_sqz_v1_h1_signal_test.csv")
