# XAU Squeeze Breakout v1 — Longs-Only Optimisation Plan

## Baseline

Load `XAUUSD Squeeze Breakout v1 - Longs Only Baseline.cbotset`. Critical locks:

- **AllowLongs = true, AllowShorts = false** — directional asymmetry confirmed by IS+OOS comparison.
- **RiskPercent = 0.5%** — fixed, never optimised.
- **MaxLeverage = 10.0**, **LeverageBuffer = 0.2** — locked.
- **MaxTradeHours = 0**, **CooldownMinutes = 0** — let the exits do the job.
- **UseDayFilter = false** — all five days.

In-sample window: **2010-01-01 → 2024-01-01** (14 years).

Reference baseline (longs-only on IS): Net **+$467** (PF **1.04**, 596 trades, win rate **38.6%**, max DD **16.7%**, avg trade $0.78).
Reference baseline (longs-only on OOS 2024+): Net **+$1,741** (PF **1.67**, 141 trades, 44% wr, max DD **2.9%**, avg trade $12.34).

The IS baseline is *break-even* — that's the honest starting point. The OOS is strong because gold has been in a structural bid since 2024. The optimiser's job is to find IS parameters that produce a meaningful per-trade edge (passes the $10 avg-trade fitness floor) without overfitting to specific historical setups.

## Custom GetFitness will pre-filter results

Already wired into the bot. Hard-rejects any combination where:

- Total trades < **40**
- Trades / year < **6**
- Net profit ≤ 0
- Max equity DD > **30%**
- **Avg trade profit < $10** ← the new lever; was the dominant failure of the IS baseline

PF capped at 3.0 in the score so absurd ratios from sparse setups don't dominate.

## What we sweep

Six parameters across three logical groups. Genetic algorithm should converge in 30 generations × 50 population.

### Tier 1 — Exit / risk geometry (the biggest levers)

| Parameter | Min | Max | Step | Why |
|---|---:|---:|---:|---|
| `SlAtrMultiple` | 2.0 | 4.0 | 0.5 | Baseline 3.0. Tighter SL = fewer stops out, but smaller losses; wider = survive noise. |
| `TpAtrMultiple` | 4.0 | 10.0 | 1.0 | Baseline 6.0. Most impactful for R:R. Wider TP rarely hits but pays bigger when it does. |
| `TrailAtrMultiple` | 2.0 | 4.0 | 0.5 | Baseline 3.0. Tighter trail cuts winners short; wider lets them run. Likely needs to be ≥ Sl multiple. |

### Tier 2 — Entry sensitivity

| Parameter | Min | Max | Step | Why |
|---|---:|---:|---:|---|
| `KCMultiplier` | 1.0 | 2.5 | 0.25 | Baseline 1.5. Tighter Keltner = more squeeze events; wider = stricter regime definition. |
| `TrendSmaPeriod` | 100 | 400 | 50 | Baseline 200. Stronger regime gate (longer SMA) filters more chop. |
| `MomentumFadeRatio` | 0.3 | 0.7 | 0.1 | Baseline 0.5. Lower = exit sooner on fade; higher = let winners run on noisy momentum. |

### Locked at default

- `BBPeriod = 20`, `BBStdDev = 2.0` — Bollinger conventions; not the levers.
- `KCPeriod = 20` — squeeze period; co-moves with KCMultiplier in effect.
- `MomentumWindow = 20` — co-moves with momentum logic; locked for stability.
- `AtrPeriod = 14` — Wilders standard.

## Fitness criteria for filtering top candidates

After the optimiser finishes, sort by **Profit Factor** (then re-sort by Net Profit as a tiebreaker). Keep candidates that satisfy ALL of:

1. **PF ≥ 1.15** on IS (baseline is 1.04; we want a meaningful step up).
2. **Total trades ≥ 60** (4+ per year × 14 years).
3. **Max DD ≤ 25%** (baseline 16.7%; allow some headroom).
4. **Avg trade ≥ $15** (above the $10 fitness floor with margin).
5. **Net profit ≥ $1,500** (3× baseline; meaningful edge).

Tag the top 5–10 by PF that pass all five. These go to walk-forward.

## Walk-forward protocol

1. Take top 5–10 IS candidates by PF.
2. Run each on OOS (2024-01-01 → today) as a **plain backtest**, no optimisation.
3. Decision rule per candidate:
   - **OOS PF ≥ 0.7 × IS PF** → robust. Keep.
   - **OOS PF < 0.7 × IS PF** → curve-fit. Reject.
   - **OOS trade count drops below 5/yr** → too narrow; reject regardless of PF.
4. From robust survivors, pick the one with the best combination of:
   - OOS PF (highest)
   - OOS DD (lowest)
   - Trade count consistency IS↔OOS
5. **One** parameter set wins. Document it. Commit it. No re-optimising.

## Anti-overfit tripwires

- "Best" IS PF > **2.5** with realistic trade count: suspicious. Real edges on retail-spread gold rarely deliver that net of costs.
- Multiple wildly different parameter combinations all reaching similar fitness: noisy surface; weak edge.
- Optimiser converges on `TrendSmaPeriod` extremes (100 or 400): re-examine. The middle range (150-300) is where regime filters are interpretable.
- `TpAtrMultiple` converges to its maximum (10.0): suggests winners need huge runs to count, which means few winners actually closing. Look at the equity curve.
- OOS trade count crashes vs IS trade count when ratio'd to years: setup definition got narrower than is real.

## Practical execution

In cTrader Optimisation tab:

- **Algorithm**: Genetic
- **Generations**: 30
- **Population**: 50
- **Fitness Criteria**: Custom (uses our `GetFitness` — **this is critical**, don't leave on Net Profit)
- **Data**: H1 bars 2010-01-01 → 2024-01-01

Combinatorial space is roughly 5 × 7 × 5 × 7 × 7 × 5 = ~8,500 combinations. Genetic should converge well within 1,500 evaluations. Expect **2–4 hours** of optimization time on a typical machine.

## When complete

Paste the top 10 by Profit Factor (just the headline metrics — Pass #, Net, PF, DD, Trades, Win %, Avg Trade) and we'll pick the candidates to walk-forward together.
