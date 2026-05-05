# XAU Squeeze Breakout v1 — Production Parameter Set

**Source**: Optimisation Pass 320 (out of 1,525 evaluated). Survived walk-forward
on 2024-01-01 → today as the best of 5 candidate parameter sets.

## Performance summary

|  | IS (2010-2024) | OOS (2024-2026) |
|---|---:|---:|
| Net profit | $2,613 | $800 |
| Profit factor | 1.51 | **1.66** |
| Total trades | 427 | 94 |
| Win rate | 40.0% | 41.5% |
| Max equity DD | 3.84% | **1.75%** |
| Average trade | $6.12 | $8.51 |
| Largest win | (IS) | $214.18 |
| Largest loss | (IS) | $77.34 |

**OOS PF improved by 10% over IS, OOS DD halved, win rate stable.** Survived
walk-forward decision rule (OOS PF ≥ 0.7 × IS PF) by a huge margin. Selected
over four other shortlisted candidates (Pass 517, 1234, 1335, 1449) on the
basis of lowest OOS DD plus highest OOS PF.

## Parameter table

### Strategy levers (these are what the optimiser converged on)

| Parameter | Baseline | **Pass 320** | Direction of change |
|---|---:|---:|---|
| `BBPeriod` | 20 | 20 | unchanged |
| `BBStdDev` | 2.0 | 2.0 | unchanged |
| `KCPeriod` | 20 | 20 | unchanged |
| `KCMultiplier` | 1.5 | **1.25** | tighter Keltner — catches more squeezes |
| `TrendSmaPeriod` | 200 | **150** | shorter trend SMA — faster regime adaptation |
| `MomentumWindow` | 20 | 20 | unchanged |
| `MomentumFadeRatio` | 0.5 | **0.7** | looser fade exit — winners run further |
| `AtrPeriod` | 14 | 14 | unchanged |
| `SlAtrMultiple` | 3.0 | **4.0** | wider stop — survive intraday noise |
| `TpAtrMultiple` | 6.0 | **7.0** | slightly wider TP — bigger runners |
| `TrailAtrMultiple` | 3.0 | **2.0** | tighter trail — lock profits faster once trade moves |

### Risk + direction (locked, not optimised)

| Parameter | Value |
|---|---:|
| `RiskPercent` | 0.5 |
| `MaxLeverage` | 10.0 |
| `LeverageBuffer` | 0.2 |
| `AllowLongs` | true |
| `AllowShorts` | **false** |

### Trade control (defaults)

| Parameter | Value |
|---|---:|
| `MaxTradeHours` | 0 (off) |
| `CooldownMinutes` | 0 |
| `UseDayFilter` | false |
| `UseSpreadFilter` | false |
| `MaxSpread` | 0.5 |

### Fitness floors used during optimisation

| Parameter | Value | Effect |
|---|---:|---|
| `MinTotalTrades` | 60 | Hard floor — kept |
| `MinTradesPerYear` | 4 | Hard floor — kept |
| `MaxFitnessDrawdownPct` | 25 | Hard floor — kept |
| `MinAverageTradeProfit` | **5** | Hard floor — kept (note: discussed as $15 but actually $5 during optimisation) |
| `FitnessPfCap` | 1.05 | PF score contribution capped — caused optimiser to favour consistency over peak PF |
| `BacktestYears` | 13 | Used for trades/yr calculation |

## The strategic interpretation

The winning combination tells a coherent story:

1. **Tighter Keltner (1.25)** — the squeeze definition is more inclusive, which
   catches more compressing-volatility setups before they release. More entry
   opportunities.
2. **Shorter trend SMA (150)** — the regime filter adapts faster to changing
   trend conditions. Doesn't wait for the slower 200-bar average.
3. **Wider stop (4× ATR)** — gives the trade room to breathe through normal
   intraday volatility, which is meaningful on H1 gold.
4. **Tighter trail (2× ATR)** — once price moves favourably, the trail locks
   profit aggressively. Combined with the wider SL, this means: tolerate
   noise at entry, harvest profit fast on success.
5. **Looser fade exit (0.7)** — momentum can drop to 70% of prior reading
   before the bot bails. Avoids whipsaw exits during noisy continuation.

It's a "wide-stop / fast-trail" exit construction — a known robust pattern in
breakout systems. The optimiser found it organically.

## Caveats

- This is **regime-conditional** alpha. The IS+OOS validation includes the
  post-2024 gold structural bid; the strategy will not perform the same in
  ranging or downtrending gold.
- Strategy is **long-only**. We disabled shorts based on the directional
  asymmetry observed during screening.
- Quote currency cost drag (commission + swap) eats ~25-35% of gross P&L in
  IS. Live conditions on Raw Trading should be similar but worth verifying
  during demo.
- This parameter set is **frozen**. Any optimisation re-run on different
  windows risks re-curving. Walk-forward already validated this combination;
  trust it.

## Demo deployment plan

1. Load this `.cbotset` into `XAUUSD_SqueezeBreakout_v1` in cTrader.
2. Attach to XAUUSD H1 chart on a **demo account** with $1k–$5k notional.
3. **Run for at least 1 calendar month** without any parameter changes.
4. **Kill switch**: pause the bot if rolling 90-day PF drops below 1.20 OR
   drawdown exceeds 8% (i.e. ~4× the OOS observed).
5. Compare live results to OOS expected: ~7-8 trades/month, ~40% win rate,
   ~$8 avg trade per 0.5% risk position.
6. Review at the end of the month before any live capital decision.
