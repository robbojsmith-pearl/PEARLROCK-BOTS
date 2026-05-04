# XAUUSD shorts-only optimisation plan (v2)

## Baseline (the locked-down config)

Load `Weekly Mean Reversion v2 - XAUUSD shorts baseline.cbotset`. Key locks:

- **AllowLongs = false, AllowShorts = true** — diagnostic confirmed shorts have the edge, longs don't
- **Day filter: Tue/Wed only** — Monday's open is the reference; Thu/Fri risk weekend exposure
- **MaxTradeHours = 48** — closes positions before weekend
- **WeeklyLossLimitPct = 3.0** — circuit breaker stays on
- **RiskPercent = 0.5%** — fixed, not optimized (anti-overfit)

In-sample window: **2016-01-01 → 2024-01-01**.

Reference run (current baseline on XAUUSD shorts): Net +1,461, PF 1.60, DD 13.3%, 52 trades, 23.1% win rate.

## What to optimize

Sweep these in cTrader's Optimization tab. Total combinations ~3,000 — keep it bounded.

| # | Parameter | Min | Max | Step | Why this range |
|---|---|---|---|---|---|
| 1 | DeviationAtrMult | 1.5 | 3.5 | 0.25 | Baseline is 2.0. Lower = more trades, weaker signal. Higher = fewer, higher-conviction setups. |
| 2 | ConfirmationBars | 1 | 4 | 1 | Baseline 2. Test whether 1 bar is enough or 3+ helps. |
| 3 | SlAtrMultiple | 1.5 | 4.0 | 0.5 | Baseline 2.0. Wider stops survive noise but increase loss size. |
| 4 | ResetBufferPips | 0 | 30 | 5 | Baseline 2. Bigger buffer prevents premature setup invalidation. Gold pip is $0.10, so 30 pips = $3. |
| 5 | TpBufferPips | 0 | 30 | 5 | Baseline 5. Test sensitivity. |
| 6 | M5AtrPeriod | 7 | 21 | 7 | Three discrete values: short, medium, long. |
| 7 | MaxTradesPerWeek | 1 | 3 | 1 | Test whether 1 trade/week is more selective and improves PF. |

## Do NOT optimize (lock these)

- `RiskPercent` — leave at 0.5%. Optimising risk size = curve fit.
- `MinTpToSlRatio` — leave at 0.5. Structural filter, not a knob.
- Day filter flags — keep Tue/Wed only.
- `MaxTradeHours` — keep 48 to enforce no-weekend-exposure.
- `WeeklyLossLimitPct` — keep 3.0.
- All direction settings — keep shorts-only.
- `D1AtrPeriod` — keep 14 (industry standard).

## Fitness criteria

cTrader will let you sort/score. I'd rank by:

1. **Profit factor ≥ 1.4** as the bar (baseline is 1.60, accept slight degradation only if other metrics improve a lot).
2. **Max equity drawdown ≤ 18%** (baseline 13.3%, give it 5pp headroom).
3. **Total trades ≥ 30** over 8 years (≥ ~4/year). Below that the result is luck.
4. **Win rate ≥ 18%** (baseline 23%; allow some giveback for higher PF).
5. **Net profit > baseline ($1,461)** otherwise why bother.

A combination that meets all five is a candidate. Take the **top 5–10 by PF**, not by net profit (PF is more robust to fluky big trades).

## Then walk-forward

This is the critical step. The IS window 2016–2024 is ~8 years. Hold out 2024–2026 as out-of-sample. **Don't peek at OOS during optimisation.**

1. Take the top 5–10 candidates from IS.
2. Run each on OOS (2024-01-01 → today).
3. Anything that's profitable on both IS and OOS with similar PF/DD is real. Anything that's great on IS but breaks on OOS was overfitted.

The chosen winner — and only one — goes to demo forward-test.

## Anti-overfit tripwires

- If your "best" optimized PF is **>3.0**, be suspicious. Real edges on noisy markets don't deliver that on out-of-sample.
- If 3+ different parameter combinations give wildly different equity curves, the surface is noisy and the strategy is fitting random patterns.
- If trade count per year drops below 4, you've optimized into the corner of the search space. Reject.
- Save every optimization run's `.optset` and results so you can audit later.

## Practical execution

In cTrader Optimization tab:
- **Algorithm: Genetic** (faster than exhaustive; converges on the ridge)
- **Generations: 30**
- **Population: 50**
- **Fitness: Net profit** (then re-rank by PF in the results grid manually)
- **Use historical ticks** if available; m1 minimum.

Expect 1–3 hours of optimization time depending on hardware.
