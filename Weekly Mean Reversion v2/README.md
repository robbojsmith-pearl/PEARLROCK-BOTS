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

## Status

- [x] v2 architecture done, 37 tests green
- [x] v2.1 (TpFraction) added, single-file builds shipped
- [x] Screening across 10 symbols complete
- [x] Three asymmetric edges identified and documented
- [ ] Optimization phase — XAU + GBP + EURGBP `.optset` files prepared, runs in progress
- [ ] Walk-forward on each winning candidate (2024-01-01 → today)
- [ ] Demo forward-test on best candidate(s)
