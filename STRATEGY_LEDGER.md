# Pearlrock Systematic — Strategy Ledger

**Single source of truth for strategy status.** When a strategy changes status,
update its entry here — and when one is discarded, record WHY it died, not its
best in-sample number. (This file exists because a hopeful in-sample figure once
got promoted to "validated live system" and caused months of confusion.)

Last updated: 2026-06-02

---

## Status legend

- **LIVE** — running on a real account
- **VALIDATED** — passed clean out-of-sample test, not yet live
- **UNVALIDATED** — code exists, no clean OOS test run yet
- **DISCARDED** — tested and failed; kept only as a reference/lesson

---

## Strategies

### Gold Double Confirm (XAUUSD Asian Box Breakout)
- **Status:** LIVE (stable baseline — fixed reference, do not alter core feature set without explicit decision)
- **Instrument / TF:** XAUUSD, M5 execution with London-session box
- **Feature set:** London-time logic; EMA(55) trend + slope filters; ATR (H1 ceiling + D1 floor); risk-based sizing with volume cap; M5 MACD zero-cross long filter; TP-in-R; skip Fridays; max trades/day; trailing stop via legacy ModifyPosition (pragma-suppressed obsolete warnings)
- **Notes:** This is the locked baseline.

### AUDJPY M5 Tuesday Mean Reversion
- **Status:** DISCARDED
- **Kill reason:** Failed out-of-sample validation. The strongest in-sample version (WICK H1 Asia Tuesday-only) was PF 2.29 over the full period but collapsed to **PF 0.66 in the 2024–2026 holdout** — overfit to the earlier regime. A later Tuesday-only AUDJPY M5 run (v2.2 Option B, from 2012) came back **PF 0.62, t-stat −2.36, profitable in only 3 of 14 years** — a significantly negative edge.
- **Never ran live.**
- **IMPORTANT:** The "PF 1.82 OOS" figure does NOT belong to this system. See note below.

### Z-Score Mean Reversion (USDCHF / XAUUSD)
- **Status:** DISCARDED
- **Origin of the "PF 1.82" figure:** Validated on **USDCHF** only — PF 1.82, DD 2.2%. **Never reproduced anywhere else.**
- **Kill reason:** Failed on XAUUSD M5 — 950 trades, PF 0.85, DD 50%.
- **Action:** Keep PF 1.82 permanently detached from the AUDJPY work. It was a single-instrument result that did not generalise.

### WICK (Liquidity Sweep / Wick Rejection Mean Reversion)
- **Status:** DISCARDED
- **Kill reason:** Rebuilt from scratch with realistic fees; tested M5/M15/M30/H1 on GBPJPY — all negative or marginal (M15 PF 0.81 DD 97%; M30 PF 0.87; H1 PF 0.90 DD 57%). A Tuesday Asia-session edge was identified but failed OOS on the 2024–2026 holdout (this is the same lineage as the AUDJPY Tuesday entry above).

### Weekly Open Mean Reversion v2
- **Status:** UNVALIDATED
- **Instrument / TF:** Symbol-agnostic, M5 execution, weekly Monday-open anchor
- **Design:** Clean DI rewrite — SessionClock (London/BST via TimeZoneInfo), WeeklyState (Monday open capture, weekly equity, circuit breaker), pure SignalEngine state machine (Idle → DeviationDetected → Confirming), pure RiskManager. Day-filter defaults to Tue+Wed on.
- **Notes:** This is a SEPARATE strand from the AUDJPY/WICK work — different signal model (deviate from Monday open, fade back). No clean OOS test on record. Needs a proper validation protocol before any live consideration.

---

## Headline status (2026-06-02)

**As of today there is NO cleanly OOS-validated live trading edge in the codebase.**
- The only LIVE system is Gold Double Confirm (baseline).
- The "validated AUDJPY system, PF 1.82 OOS" referenced in older notes does not exist as described — that number was a USDCHF Z-score result, and the AUDJPY work was correctly discarded on OOS failure.

## Lesson recorded
When a strategy is discarded, the surviving note must state the **kill reason and the
failing OOS number**, never just the best in-sample stat. Optimistic figures that
outlive their kill reason are how a dead strategy gets resurrected as a "keeper."
