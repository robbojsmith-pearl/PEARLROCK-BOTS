# XAUUSD Squeeze Breakout v1

Long/short volatility-compression breakout strategy for XAUUSD on H1 bars.
Ports the LinearAlphaKCSqueeze concept from QuantConnect into our cTrader v2-style
architecture (Core / Adapters / cBot, xUnit-tested), with both directions enabled
and the v2 walk-forward methodology applied from day one.

## Strategy at a glance

A trade is opened on the **bar after a Bollinger-Band-inside-Keltner-Channel
squeeze just released**, when ALL three of these align in one direction:

|                  | LONG                              | SHORT                             |
|------------------|-----------------------------------|-----------------------------------|
| Trend gate       | price > 200H SMA                  | price < 200H SMA                  |
| Momentum sign    | LR projection > 0                 | LR projection < 0                 |
| Momentum slope   | curr > prev (strengthening up)    | curr < prev (strengthening down)  |

**Stop**: 3×ATR. **TP**: 6×ATR. **Trail**: ratcheting at 3×ATR.
**Soft exit**: momentum loses sign or fades >50% from prior bar.

One position at a time. No pyramiding.

## Why this strategy

v2's Weekly Mean Reversion was shelved after walk-forward (see the v2 folder's
post-mortem). The lesson: mean reversion fails in trending markets, especially
on gold's structural bid post-2024. **This strategy is the inverse hypothesis** —
ride breakouts with the trend rather than fade them — and has the regime gate
(200H SMA) baked into the entry logic.

## Architecture

Forked self-contained copy of v2's pattern:

- `Core/` — pure C# logic, no cAlgo dependencies. Unit-testable.
- `Adapters/` — wrap cAlgo `Bars`, `Server.Time`, `Symbol` behind Core interfaces.
- `XAUUSD_SqueezeBreakout_v1.cs` — thin orchestrator wiring it all together.
- `Core/Fitness.cs` — extended from v2 with `MinAverageTradeProfit` to reject
  strategies whose per-trade edge is too thin for real spreads.

## Backtest plan

- **IS**: 2010-01-01 → 2024-01-01 (14 years on H1). Spans 2011 gold peak,
  2013 crash, 2015 grind, 2020 COVID, 2022 inflation, 2024+ structural bid.
- **OOS**: 2024-01-01 → today. Held out from optimisation.
- **Risk per trade**: 0.5% (locked).
- **Custom GetFitness on from day 1.**

## Status

- [x] Architecture sketched
- [ ] Project scaffolded
- [ ] Core classes + tests written
- [ ] cBot orchestrator written
- [ ] Single-file build for cTrader paste
- [ ] Baseline backtest 2010-2024
- [ ] Walk-forward 2024+
- [ ] Optimisation (with custom GetFitness)
- [ ] OOS validation of optimisation winners
- [ ] Demo / live decision
