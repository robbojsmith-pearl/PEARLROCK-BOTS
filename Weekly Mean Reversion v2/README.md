# Weekly Mean Reversion v2 / v2.1

Mean-reversion-to-Monday-open cBot for cTrader, plus the research that surrounded
its second-generation rebuild.

## What's in this folder

```
Weekly Mean Reversion v2/
  README.md                              ← you are here
  bots/
    Weekly Mean Reversion v2 (single file).cs       ← canonical v2 cBot
    Weekly Mean Reversion v2.1 (single file).cs     ← v2 + TpFraction param
  project/                                          ← multi-file project + xUnit tests
    Weekly Mean Reversion v2.sln
    Weekly Mean Reversion v2/                       ← cBot project (Core, Adapters)
    Weekly Mean Reversion v2.Tests/                 ← xUnit project (37 tests)
  configs/                                          ← starting cBot settings
    *.cbotset files (screening defaults + per-leg baselines)
  optsets/                                          ← cTrader optimization presets
    *.optset files (per-leg sweep configurations)
  plans/                                            ← optimization plans (markdown)
    *.md files (param ranges, fitness criteria, walk-forward protocol)
  research/
    Weekly Mean Reversion v2 - Screening.xlsx      ← screening + diagnostic results
```

## Architecture (v2)

v1 was a single 500-line cBot. v2 is split so the strategy logic is unit-testable
without touching cTrader:

- **Core** — pure C# logic, no cAlgo dependencies
  - `SessionClock` — UTC ↔ London via `TimeZoneInfo` (replaces v1's hand-rolled BST)
  - `WeeklyState` — Monday-open detection, week-equity tracking, circuit breaker
  - `SignalEngine` — explicit state machine: Idle → DeviationDetected → Confirming
  - `RiskManager` — pure SL/TP/volume calculator
- **Adapters** — wrap cAlgo `Bars`, `Server.Time`, `Symbol` behind Core interfaces
- **WeeklyOpen_MeanReversion_v2** — thin orchestrator wiring it all together
- **Tests** — xUnit project links the Core files; 37 tests across the four classes

### Bug fixes vs v1

1. **Monday open detection.** v1 read `Bars.OpenPrices.Last(1)` on the first Monday
   bar `OnBar` saw — which returned the *previous* M5 bar, not Monday's first bar.
   v2 scans M5 history for the earliest closed bar that falls on London Monday.
   Works correctly when bot starts mid-week.
2. **BST handling.** v1's `IsBST` was hand-rolled and fragile near transitions.
   v2 uses `TimeZoneInfo.ConvertTimeFromUtc` (Europe/London / GMT Standard Time).
3. **Premature signal reset.** v1 reset on any close back through Monday open.
   v2 only resets when the close is back through *plus* a configurable buffer.

## v2.1 (TpFraction)

Adds one parameter:

```csharp
[Parameter("TP Fraction (0.05..1, of entry->Monday open distance)",
           DefaultValue = 1.0, MinValue = 0.05, MaxValue = 1.0, Step = 0.05, Group = "Risk")]
public double TpFraction { get; set; }
```

- `TpFraction = 1.0` → TP at Monday open (v2.0 behaviour, default)
- `TpFraction = 0.5` → TP halfway between entry and Monday open

The screening showed this is **symbol-specific** — see results below. v2.1 is the
canonical cBot going forward; set `TpFraction = 1.0` to mimic v2.0.

## The screening exercise

Backtested on 10 symbols over 2016-01-01 → 2024-01-01, M5 timeframe, $10k start:
EURUSD, USDJPY, GBPUSD, AUDUSD, USDCAD, EURGBP, US500, XAUUSD, XAGUSD, AUDJPY.

### Default screening (TpFraction = 1.0)

9 of 10 unprofitable. Win rates 5–17% across the board. Only **XAUUSD** profitable
(PF 1.26, +14%). The math: SL ≈ 2× M5 ATR (10–20 pips), TP ≈ Monday open
(60–100 pips away) → R:R around 12:1, requiring ~9% win rate to break even — and
most symbols sit right at that threshold, so spread/commission tips them negative.

### Diagnostic A — TpFraction = 0.5

Halving the TP target to test partial mean reversion. Win rates rose 2–5pp, PF
improved on every symbol, drawdowns dropped 3–7pp — but the lift wasn't enough
to push anything across breakeven on the major FX pairs.

### Diagnostic B — XAUUSD shorts only

Removing longs improved every metric: PF 1.26 → 1.60, DD 20.8% → 13.3%,
win rate 16.3% → 23.1%. Confirmed the gold edge is **directionally asymmetric**.

### Diagnostic D — direction asymmetry across candidates

| Leg | Bot | TpFraction | Net | PF | Max DD | Win % | Verdict |
|---|---|---:|---:|---:|---:|---:|---|
| **XAUUSD shorts** | v2 | 1.0 | +$1,461 | 1.60 | 13.3% | 23.1% | KEEP |
| **GBPUSD longs**  | v2 | 1.0 | +$165 | 1.07 | 15.9% | 15.8% | KEEP (marginal) |
| GBPUSD longs | v2.1 | 0.5 | -$428 | 0.82 | 11.1% | 15.4% | reject (full reversion is the GBP edge) |
| EURGBP longs | v2 | 1.0 | -$470 | 0.78 | 9.3% | 23.7% | reject |
| **EURGBP longs** | v2.1 | 0.5 | -$9 | 1.00 | 7.6% | 27.0% | KEEP (marginal — needs spread filter) |

Three confirmed asymmetric directional edges, each on a different `TpFraction`
setting. They cover gold, cable, and an FX cross — low correlation, so this is
viable as a small portfolio.

## Optimization plans

Each leg has its own `.optset` and a markdown plan in `plans/` listing the
parameters to sweep, the locks, fitness criteria, and the walk-forward protocol.
Common locks across all three plans:

- `RiskPercent = 0.5` (never optimised — anti curve-fit)
- Day filter: Tue/Wed only
- `MaxTradeHours = 48` (no weekend exposure)
- `WeeklyLossLimitPct = 3.0` (circuit breaker)
- `MinTpToSlRatio = 0.5`
- Direction (long/short) locked per leg

Walk-forward window: **2024-01-01 → today** (out of sample, never peeked at
during optimization).

## Running tests locally

```bash
cd "Weekly Mean Reversion v2/project"
dotnet test ".\Weekly Mean Reversion v2.Tests\Weekly Mean Reversion v2.Tests.csproj"
```

Requires .NET 6 SDK or later. 37 tests across `SessionClockTests`,
`WeeklyStateTests`, `SignalEngineTests`, `RiskManagerTests`.

## Importing into cTrader

Either:
- (a) **Single-file builds** in `bots/` — paste straight into a new cBot in cTrader
  Automate. Compile, attach to an M5 chart, load a `.cbotset` from `configs/`.
- (b) **Multi-file project** in `project/` — open the `.sln` in cTrader Automate's
  IDE if you want to read or modify Core/Adapters separately.

## Status — SHELVED after walk-forward

- [x] v2 architecture done, 37 tests green
- [x] v2.1 (TpFraction) added, single-file builds shipped
- [x] Custom `GetFitness` ported (rejects under-trading curve-fits at the optimizer level)
- [x] Screening across 10 symbols complete
- [x] Three asymmetric edges identified and documented
- [x] Optimization phase — XAU + GBP + EURGBP all run
- [x] Walk-forward on each candidate (2024-01-01 → today)
- [ ] ~~Demo forward-test~~ — **all three legs failed OOS; nothing deployed**

## Post-mortem

After completing optimisation and walk-forward (2024-01-01 → today, ~16-28 months OOS),
all three asymmetric edges identified during screening have been **shelved**. The
strategy is not deployable in the current market regime.

### Final results

| Leg | IS (2016-2024) | OOS verdict | Cause |
|---|---|---|---|
| XAUUSD shorts (TpFraction = 1.0) | PF 1.60, +$1,461, 23% wr, 13.3% DD | PF 0.79, -4%, 8% wr | Regime shift — gold's structural bid post-2024 (central bank flows, geopolitical premium) kills short-biased mean reversion. Both optimised candidates AND baseline failed identically, ruling out curve-fit. |
| GBPUSD longs (TpFraction = 1.0) | PF 1.07, +$165, 16% wr, 15.9% DD | PF 0.0, 0 wins / 4 trades, -2% | Edge evaporated. Baseline produced no winners across 16 months OOS. |
| EURGBP longs (TpFraction = 0.5) | PF 1.00, -$9, 27% wr, 7.6% DD | 2 trades / 28 months on optimised; baseline skipped | Always too sparse; optimisation narrowed entry conditions further into a setup that barely fires. |

### What we learned

1. **Mean reversion to a weekly anchor is regime-dependent.** It needs a two-sided
   ranging market. Once one side is structurally bid (gold post-2024), short-biased
   reversion gets crushed. The XAU baseline OOS PF of 0.79 confirmed this isn't a
   curve-fitting issue — the underlying edge died with the regime.

2. **Walk-forward is non-negotiable.** 8 years of clean IS data on three different
   markets gave a PF range of 1.00-1.60 — looked like real edges. They weren't.
   Without the OOS step, we'd have committed capital to losers.

3. **Optimisation without a custom fitness invites overfit.** Both the GBPUSD and
   EURGBP optimisation runs converged on under-trading combinations (4-5 trades/year,
   16-33 trades over 8 years) that fail by definition: too few samples to validate,
   too narrow a setup definition to recur. The custom `GetFitness` since added
   (`MinTradesPerYear ≥ 6`, PF capped at 3.0) hard-rejects those candidates at the
   optimiser level so the results grid is pre-filtered next time around.

4. **For v3, every mean-reversion strategy needs a regime gate.** A simple D1
   trend filter (skip shorts when price > D1 200-SMA + N×ATR; skip longs in the
   inverse) would have saved the XAU leg in the OOS window. This is the single
   most important unlearned lesson from v2.

### What survived

- **The codebase.** v2's Core / Adapters / cBot architecture, the xUnit suite
  (49 tests including the new Fitness ones), `TpFraction`, and the custom
  `GetFitness` filter all carry forward to whatever strategy comes next.
- **The methodology.** Default screening → diagnostic A/B/D → optimisation with
  custom fitness → walk-forward IS *and* baseline → ship or shelve. Repeatable
  in a few days for the next idea.
- **No live capital was at risk.** The entire exercise was backtest +
  optimisation + walk-forward. The bot was never deployed. That's the
  walk-forward step paying for itself in this single cycle.

The v2.1 cBot remains importable in cTrader. If the gold regime ever shifts back
to two-sided ranging, or if a future test on different symbols/timeframes shows
promise, this folder is the starting point — not a redesign.
