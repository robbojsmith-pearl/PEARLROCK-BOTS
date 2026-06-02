Pearlrock Systematic — Strategy Ledger
Single source of truth for strategy status. When a strategy changes status,
update its entry here — and when one is discarded, record WHY it died, not its
best in-sample number. (This file exists because a hopeful in-sample figure once
got promoted to "validated live system" and caused months of confusion.)
Last updated: 2026-06-02

Status legend

LIVE — running on a real account
DEMO — running forward on a demo account, no live capital
VALIDATED — passed clean out-of-sample test, not yet deployed
UNVALIDATED — code exists, no clean OOS test run yet
DISCARDED — tested and failed; kept only as a reference/lesson


Strategies
Gold Double Confirm (XAUUSD Asian Box Breakout)

Status: LIVE (stable baseline — fixed reference, do not alter core feature set without explicit decision)
Instrument / TF: XAUUSD, M5 execution with London-session box
Feature set: London-time logic; EMA(55) trend + slope filters; ATR (H1 ceiling + D1 floor); risk-based sizing with volume cap; M5 MACD zero-cross long filter; TP-in-R; skip Fridays; max trades/day; trailing stop via legacy ModifyPosition (pragma-suppressed obsolete warnings)
Notes: This is the locked baseline.

XAUUSD Squeeze Breakout v1 (repo: xauusd-squeeze-v2)

Status: LIVE (real money), ~1 month as of 2026-06-02 — but running at FIXED 0.01 lots, NOT the validated 0.5% ATR risk-based sizing, and ALSO running on BTCUSD (untested — see caveats). So this is the validated entry logic live, with the sizing model switched off, plus an unvalidated second instrument. NOT a clean live run of the validated system.
Live-deployment caveats:

Fixed 0.01 lots bypasses the validated risk model. The strategy was validated on 0.5% risk-based ATR sizing (size scales inversely with stop distance). At fixed lots, dollar-risk varies trade to trade, so live results will NOT map onto OOS expectations (~7–8 trades/mo, ~$8 avg per 0.5% position). For a min-size live shakedown this is reasonable, but it is a different system than the one that passed walk-forward.
BTCUSD was never validated. The squeeze is gold-H1-only; the Bitcoin leg is an unvalidated live experiment on an instrument with completely different volatility/session/gap behaviour. Track it SEPARATELY, not as part of "the validated squeeze."
Trades not yet logged trade-by-trade. PRIORITY: pull cTrader closed-trade history for the month and tag by symbol before it ages out, so gold and BTC performance can be separated and compared to OOS.


Instrument / TF: XAUUSD, H1. Long-only.
Design: Bollinger-inside-Keltner squeeze release + trend gate (price vs 200H SMA, optimised to 150) + linear-regression momentum projection (sign + slope). SL 3×ATR (opt 4×), TP 6×ATR (opt 7×), ratcheting ATR trail, soft exit on momentum fade. Core/Adapters/cBot architecture, xUnit-tested.
Validation: Production set = Optimisation Pass 320 (selected from 1,525 evaluated; best of 5 walk-forward candidates).

IS (2010–2024): PF 1.51, +$2,613, 427 trades, 40.0% win, 3.84% DD.
OOS (2024–2026): PF 1.66, +$800, 94 trades, 41.5% win, 1.75% DD.
OOS PF improved over IS and OOS DD halved; passed walk-forward rule (OOS PF ≥ 0.7×IS PF) by a wide margin.


CAVEAT — regime-conditional: This is explicitly NOT regime-agnostic alpha. By its own production notes it depends on gold's post-2024 structural bid; it "will not perform the same in ranging or downtrending gold." Long-only, validated on a window that includes the regime it exploits. Treat the edge as conditional on the current gold regime persisting.
Cost drag: commission + swap eat ~25–35% of gross P&L in IS; verify on Raw during demo.

XAUUSD Squeeze Breakout v2 — Regime Overlay (repo: xauusd-squeeze-v2)

Status: VALIDATED — slightly better risk-adjusted than v1 on OOS; default config (v2_baseline) selected to ship.
Design: v1 plus a daily macro-regime overlay. VIX/DXY/SPY features (built by generate_regime_csv.py) classify Risk-On / Neutral / Risk-Off / Panic / Unknown and scale RiskPercent (1.25 / 1.00 / 0.50 / 0.00-skip / 0.50). Position-size only — does not change which trades are taken, except skipping new entries in Panic.
Validation (OOS 2024–2026): v2 defaults PF 1.75, DD 1.52%, worst −$82 — beat v1 baseline on Calmar, PF, and worst-loss. Optimised Pass 21 was indistinguishable from v1; Pass 186 (1.5% risk) was IS overfit. Default config won → ship default.
Operational dependency: cBot reads regime.csv from local disk; needs the Python feature script scheduled daily (Task Scheduler, 22:30 local) on the same machine / synced folder. Requires AccessRights.FullAccess in cTrader.
Same regime caveat as v1 applies.
NOTE: The v3 - Squeeze Breakout folder inside PEARLROCK-BOTS is a STALE early snapshot (status checklist shows project barely started). The xauusd-squeeze-v2 repo is the current truth for this strategy.

Weekly Mean Reversion v2 / v2.1 (mean-reversion-to-Monday-open)

Status: DISCARDED (was previously mislabelled UNVALIDATED here — it WAS walk-forward tested and failed)
Design: Symbol-agnostic, M5, weekly Monday-open anchor; deviate then fade back. Clean DI architecture (SessionClock, WeeklyState, SignalEngine state machine, RiskManager), 37–49 xUnit tests, custom GetFitness.
Screening (2016–2024): 9 of 10 symbols unprofitable. Three asymmetric edges identified: XAUUSD shorts (PF 1.60), GBPUSD longs (PF 1.07, marginal), EURGBP longs TpFraction 0.5 (PF 1.00, marginal).
Kill reason — all three failed OOS (2024-01-01 → today):

XAUUSD shorts: PF 1.60 → 0.79 (−4%). Gold's post-2024 structural bid kills short-biased reversion. Baseline failed identically to optimised → NOT curve-fit, the edge died with the regime.
GBPUSD longs: PF 1.07 → 0.0 (0 wins / 4 trades over 16 months).
EURGBP longs: too sparse to validate (2 trades / 28 months optimised).


Never deployed. No live capital risked. Walk-forward paid for itself.
What survived: the codebase/architecture, the xUnit suite, TpFraction, the custom GetFitness — all carried forward into the Squeeze Breakout work.

AUDJPY M5 Tuesday Mean Reversion

Status: DISCARDED
Kill reason: Failed out-of-sample validation. Strongest in-sample version (WICK H1 Asia Tuesday-only) was PF 2.29 over the full period but collapsed to PF 0.66 in the 2024–2026 holdout — overfit to the earlier regime. A later Tuesday-only AUDJPY M5 run (v2.2 Option B, from 2012) came back PF 0.62, t-stat −2.36, profitable in only 3 of 14 years — significantly negative.
Never ran live.
IMPORTANT: The "PF 1.82 OOS" figure does NOT belong to this system. See Z-Score entry below.

Z-Score Mean Reversion (USDCHF / XAUUSD)

Status: DISCARDED
Origin of the "PF 1.82" figure: Validated on USDCHF only — PF 1.82, DD 2.2%. Never reproduced anywhere else.
Kill reason: Failed on XAUUSD M5 — 950 trades, PF 0.85, DD 50%.
Action: Keep PF 1.82 permanently detached from the AUDJPY work. Single-instrument result that did not generalise.

WICK (Liquidity Sweep / Wick Rejection Mean Reversion)

Status: DISCARDED
Kill reason: Rebuilt with realistic fees; M5/M15/M30/H1 on GBPJPY all negative/marginal (M15 PF 0.81 DD 97%; M30 PF 0.87; H1 PF 0.90 DD 57%). Tuesday Asia-session edge identified but failed OOS on the 2024–2026 holdout (same lineage as the AUDJPY Tuesday entry above).


Headline status (2026-06-02)

LIVE: Gold Double Confirm (XAUUSD Asian Box Breakout) — the baseline.
LIVE (real money, ~1 month): Squeeze Breakout v1 on XAUUSD + BTCUSD at FIXED 0.01 lots. The gold leg = validated entry logic at min size (defensible shakedown). The BTC leg = unvalidated, untested instrument (track separately). Neither is running the validated 0.5% ATR sizing. Trades not yet logged — recovering and tagging the cTrader history by symbol is the immediate priority.
VALIDATED (in backtest/walk-forward), not yet deployed as-validated: Squeeze v1 at 0.5% sizing, and v2 (regime overlay). v2 is marginally stronger risk-adjusted on OOS.
The squeeze edge is regime-conditional (gold's post-2024 structural bid), long-only. Real and cleanly walk-forward validated on gold, but not regime-agnostic and not validated outside gold.
DISCARDED on OOS failure: Weekly Mean Reversion v2, AUDJPY Tuesday, Z-Score MR, WICK.
The old "validated AUDJPY system, PF 1.82 OOS" does NOT exist as described — that number was a USDCHF Z-score result, and the AUDJPY work was correctly discarded.

Lesson recorded
When a strategy is discarded, the surviving note must state the kill reason and the
failing OOS number, never just the best in-sample stat. Optimistic figures that
outlive their kill reason are how a dead strategy gets resurrected as a "keeper."
Conversely — keep this ledger in sync with the repos: the squeeze breakout was the
strongest validated edge in the codebase and was missing from the first version of
this file entirely
