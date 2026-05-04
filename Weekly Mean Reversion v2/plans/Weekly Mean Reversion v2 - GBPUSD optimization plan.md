# GBPUSD longs-only optimisation plan (v2 / TpFraction = 1.0)

## Baseline

Load `Weekly Mean Reversion v2 - GBPUSD longs baseline.cbotset`. Key locks:

- **AllowLongs = true, AllowShorts = false** — directional asymmetry confirmed; cable longs are the edge.
- **TpFraction = 1.0** — full reversion to Monday open, what works on GBPUSD per Diagnostic A.
- **Day filter: Tue/Wed only** — Monday is the reference, Thu/Fri risk weekend exposure.
- **MaxTradeHours = 48** — closes positions before weekend.
- **WeeklyLossLimitPct = 3.0** — circuit breaker on.
- **RiskPercent = 0.5%** — fixed, never optimized (anti-overfit).

In-sample window: **2016-01-01 → 2024-01-01**.

Reference run: Net **+$165**, PF **1.07**, DD **15.9%**, 38 trades, **15.8% win rate**.

A reminder: this is a marginal edge. The win condition is "more decisively profitable" (PF ≥ 1.3, net ≥ $500), not "more impressive numbers". If the optimizer gives you PF 2.5 with 12 trades over 8 years, that's curve fit, not improvement.

## What to optimize

| # | Parameter | Min | Max | Step | Why |
|---|---|---|---|---|---|
| 1 | DeviationAtrMult | 1.5 | 3.5 | 0.25 | Baseline 2.0. Lower = more entries; higher = more selective. |
| 2 | ConfirmationBars | 1 | 4 | 1 | Baseline 2. Test whether more confirmation tightens it up. |
| 3 | SlAtrMultiple | 1.5 | 4.0 | 0.5 | Baseline 2.0. Cable's M5 ATR is similar to other majors; wider stops survive noise but risk bigger losses. |
| 4 | ResetBufferPips | 0 | 30 | 5 | Baseline 2. Important on cable — can flip-flop around Monday open. Bigger buffer prevents premature signal reset. |
| 5 | TpBufferPips | 0 | 30 | 5 | Baseline 5. |
| 6 | M5AtrPeriod | 7 | 21 | 7 | Three values: short, medium, long. |
| 7 | MaxTradesPerWeek | 1 | 3 | 1 | Cable's edge could be more selective — test 1/wk for higher quality. |

## Locked (do not optimize)

- `RiskPercent` = 0.5% — optimising risk size = curve fit.
- `MinTpToSlRatio` = 0.5 — structural filter.
- `TpFraction` = 1.0 — symbol-specific, locked from screening data.
- Day filter, `MaxTradeHours`, `WeeklyLossLimitPct` — safety constants.
- Direction settings — locked to longs-only.
- `D1AtrPeriod` = 14 — keep standard.

## Fitness criteria

For GBPUSD, lower thresholds than XAU since the baseline edge is weaker. Rank candidates by these floors:

1. **Profit factor ≥ 1.3** (baseline 1.07; want a meaningful step up).
2. **Max equity drawdown ≤ 22%** (baseline 15.9%; allow ~6pp headroom).
3. **Total trades ≥ 25** over 8 years (~3/year minimum).
4. **Win rate ≥ 14%** (baseline 15.8%; small giveback ok if PF rises).
5. **Net profit ≥ $500** (baseline +$165; want at least 3x baseline).

Anything that fails ≥1 of these gets dropped. Of those that pass, **rank by PF, not net profit** — net is fluky on small-trade-count strategies.

## Walk-forward

After optimization completes:

1. Take the **top 5–10 candidates by PF** from IS.
2. Run each on OOS (**2024-01-01 → today**).
3. Anything where IS PF and OOS PF agree to within ~25% is real. Wider gaps mean overfitting.
4. Choose **one** parameter set. Document it. Commit it. Never re-optimise without an explicit reason.

## Anti-overfit tripwires

- If "best" optimised PF > 2.5 with reasonable trade count: suspicious. Cable just isn't that clean.
- If 3+ very different parameter combinations all hit similar fitness: surface is noisy, edge is weak. Reject the optimisation.
- Trade count <3/year on the winning combination → you've optimised into a sparse corner. Reject.
- If the OOS run is materially worse than IS: the IS edge was illusory.

## Practical execution

cTrader Optimization tab settings:
- Algorithm: **Genetic**
- Generations: **30**
- Population: **50**
- Fitness: **Net profit** (then re-rank by PF in results grid)
- Use **historical ticks** if available, m1 minimum

Expect **1–3 hours** of optimization time on a normal machine.

## When you're done

Paste the top 10 by PF (just the headline metrics — PF, net, DD, trades, win%) and we'll pick the candidate together. Then EURGBP gets the same treatment with TpFraction = 0.5 + spread filter on.
