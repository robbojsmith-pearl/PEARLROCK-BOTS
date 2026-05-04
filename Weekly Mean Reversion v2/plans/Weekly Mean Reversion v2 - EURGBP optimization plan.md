# EURGBP longs-only optimisation plan (v2.1 / TpFraction = 0.5)

## Baseline

Load `Weekly Mean Reversion v2 - EURGBP longs baseline.cbotset`. Key locks:

- **AllowLongs = true, AllowShorts = false** — directional asymmetry; long bias only.
- **TpFraction = 0.5** — partial reversion is the edge here (full reversion failed).
- **UseSpreadFilter = true, MaxSpread = 0.00015 (1.5 pips)** — *this is the critical change*. The unfiltered baseline's avg trade is -0.23 pips, i.e. commission/spread is what's tipping a gross-breakeven strategy negative. Filtering high-spread moments should rescue it.
- **Day filter: Tue/Wed only**, **MaxTradeHours = 48**, **WeeklyLossLimitPct = 3.0** — same safety constants.
- **RiskPercent = 0.5%** — fixed.

In-sample window: **2016-01-01 → 2024-01-01**.

Reference run (v2.1 longs-only, no spread filter): Net **-$8.69**, PF **1.00**, DD **7.59%**, 37 trades, **27.0% win rate**.

The win condition here is unique: **convert PF 1.00 to PF ≥ 1.2 by filtering out commission-killer trades.** This is the cleanest equity profile of the three legs (lowest DD, highest win rate) — it just needs the spread cost trimmed.

## What to optimize

| # | Parameter | Min | Max | Step | Why |
|---|---|---|---|---|---|
| 1 | DeviationAtrMult | 1.5 | 3.5 | 0.25 | Baseline 2.0. EURGBP is slow; lower thresholds may catch enough setups. |
| 2 | ConfirmationBars | 1 | 4 | 1 | Baseline 2. |
| 3 | SlAtrMultiple | 1.5 | 4.0 | 0.5 | Baseline 2.0. EURGBP M5 ATR is small (~3-5 pips) so SL doesn't need to be wide. |
| 4 | ResetBufferPips | 0 | 20 | 5 | Smaller range than majors — EURGBP doesn't whip around Monday open as much. |
| 5 | TpBufferPips | 0 | 15 | 5 | Smaller buffer because the TP target is already short (50% of distance). |
| 6 | M5AtrPeriod | 7 | 21 | 7 | Three values. |
| 7 | TpFraction | 0.3 | 0.7 | 0.1 | Narrow sweep around the known-good 0.5 to fine-tune. |
| 8 | MaxTradesPerWeek | 1 | 3 | 1 | Test selectivity. |
| 9 | **MaxSpread** | **0.00005** | **0.00020** | **0.00005** | **0.5 to 2.0 pips. The headline lever** — too tight = no trades; too loose = no filter. |

## Locked

- `RiskPercent`, `MinTpToSlRatio`, `D1AtrPeriod` — same locks as the other plans.
- Direction — longs-only.
- `UseSpreadFilter = true` — never disabled.
- Day filter, `MaxTradeHours`, `WeeklyLossLimitPct` — safety.

## Fitness criteria

EURGBP needs different thresholds because the trade count is lower and the edge starts at gross breakeven:

1. **Profit factor ≥ 1.2** (baseline 1.00; meaningful step up).
2. **Max equity drawdown ≤ 12%** (baseline 7.6%; allow ~5pp headroom).
3. **Total trades ≥ 20** over 8 years (~2.5/year — *low*, so accept this with eyes open).
4. **Win rate ≥ 22%** (baseline 27%; some giveback ok).
5. **Net profit ≥ $300** (baseline -$9; want clearly positive).

The trade-count threshold is the soft spot here. EURGBP only fires ~5 trades/year at baseline. If a tight spread filter cuts it to 2/year, the strategy is statistically empty even if the equity curve looks pretty.

## Walk-forward

Same protocol as XAU/GBP: top 5–10 by PF on IS, run on **2024-01-01 → today** OOS, pick one. **Do not optimize on OOS.**

For EURGBP specifically: pay extra attention to OOS trade count. If it drops to 0–1 trades over the OOS window, the IS optimization fitted to specific historical setups that don't recur.

## Anti-overfit tripwires

- "Best" PF > 2.0 with low trade count (<25 over 8 years): suspicious.
- MaxSpread tightens to 0.5 pips and trade count halves: you've optimised into a corner; trade count loss outweighs spread savings.
- TpFraction fitting to 0.7+: that contradicts the screening signal — partial reversion is what works on EURGBP, not full. Reject.
- Multiple very different combinations all hit similar fitness: noisy surface; weak edge.

## Practical execution

cTrader Optimization tab:
- **Algorithm: Genetic**, **Generations: 30**, **Population: 50**
- Fitness: **Net profit**, then re-rank by PF in the results grid
- Use **historical ticks** if available; m1 minimum

Combinatorial space is bigger than XAU/GBP because of the extra `MaxSpread` and `TpFraction` sweeps — expect **2–4 hours** of optimization time on a normal machine.

## When done

Paste top 10 by PF along with the optimal `MaxSpread` for each. We'll pick the winner and walk it forward.
