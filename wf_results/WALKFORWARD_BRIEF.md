# XAU_SQZ_V1 Fixed-Parameter Monthly Walk — Brief for Claude Code / ctrader-cli

## Context
Validating XAU_SQZ_V1 (live XAUUSD Squeeze Breakout bot) by running one backtest per
calendar month from 2020-01 through the current month, using the bot's CURRENT LIVE
parameters unchanged throughout (no re-optimization — cTrader CLI's `optimize` command
isn't available until v5.10; installed version here is 5.9.0.3). Goal: see whether this
exact configuration holds up consistently month to month across different regimes,
or whether there are periods where it falls apart.

## KNOWN BUG — read this first
On this installed CLI version (5.9.0.3), invoking `ctrader-cli backtest` with
`--start=/--end=/--balance=/--report-json=` etc. as command-line flags does NOT apply
them. Instead it silently drops into an interactive "Backtest cBot — enter parameters"
wizard and DEFAULTS every value (start/end default to the last 7 days ending today,
balance defaults to 10000), then fires the job in the background. Confirmed by running
several supposedly-different date ranges and getting byte-identical results every time
(all were actually testing the same trailing-7-day window).

CONSEQUENCE FOR THIS TASK: do not just pass --start/--end as flags and assume they took
effect. For each month, explicitly drive the interactive parameter prompts (or otherwise
confirm — e.g. by checking the "Collected parameters" echo it prints before running —
that Start/End/Balance/DataMode/ReportJson genuinely match what was intended for that
fold) rather than trusting the flags alone. Verify at least the first 2-3 folds produce
DIFFERENT results (different trade counts/dates) before trusting the rest of the run.

There's also a job queue — background backtest runs stack up (seen "95 unread" from
prior failed attempts) and can be viewed with F2 or cancelled with `stop <job-id>`.
Worth clearing stale/junk jobs from earlier attempts before starting the real run.

## Target
- cBot: XAUUSD_SqueezeBreakout_v1.algo (BotLabel: XAU_SQZ_V1)
- Path: ~/PEARLROCK-BOTS-REPO/XAUUSD_SqueezeBreakout_v1.algo
- Symbol: XAUUSD, timeframe: h1
- Account: 2219914 (Raw Trading Ltd, USD — LIVE account; this is a historical
  simulation only, but confirm you're comfortable using the live account for
  repeated CLI test runs rather than switching to a demo account)
- ctid: robbojsmith@me.com
- pwd-file: ~/.ctrader-cli/pwd.txt
- Data mode: m1

## Fixed parameters (from XAUUSD_SqueezeBreakout_v1_XAUUSD_h1.cbotset — use exactly these,
unchanged, for every fold)
  BBPeriod=24
  BBStdDev=1.5
  KCPeriod=24
  KCMultiplier=1.9
  TrendSmaPeriod=200
  MomentumWindow=20
  MomentumFadeRatio=0.7
  AtrPeriod=20
  SlAtrMultiple=2.0
  TpAtrMultiple=5.0
  TrailAtrMultiple=3.6
  RiskPercent=1.0
  MaxLeverage=10.0
  LeverageBuffer=0.2
  AllowLongs=true
  AllowShorts=true
  UseDayFilter=false
  UseSpreadFilter=false
  MinTotalTrades=10
  MinTradesPerYear=6.0
  MaxFitnessDrawdownPct=30.0
  FitnessPfCap=3.0
  MinAverageTradeProfit=10.0

## Fold structure
One backtest per calendar month, 2020-01 through the current month (~80 folds):
  - Fold "2020-01": start = 01/01/2020, end = 01/02/2020 (i.e. first-of-month to
    first-of-next-month, UTC)
  - ... step forward one month at a time through the current month.

## Output convention
Save each fold's JSON report to:
  ~/PEARLROCK-BOTS-REPO/wf_results/fixed_YYYY-MM_backtest.json
(matching the naming already used by the existing partial/junk results in that folder —
those existing fixed_2020-01 through fixed_2021-03 files are ALL INVALID due to the bug
above and should be overwritten/regenerated, not trusted.)

## Methodology notes for interpreting results afterward
1. Each monthly backtest starts from the account's current live balance rather than a
   properly compounding series (separate invocations can't carry equity forward), so
   raw dollar netProfit is not comparable/summable across folds — only each fold's ROI%
   is a fair cross-fold metric. An aggregator that chains ROI% multiplicatively into a
   synthetic compounded walk-forward equity curve already exists:
   ~/PEARLROCK-BOTS-REPO/aggregate_fixed_walk.py (run with `python3` once folds exist).
2. A trade still open at a month's cutoff gets marked to market in that fold but doesn't
   carry into the next fold's (independent, flat-start) backtest — minor boundary
   artifact, not fixable without chaining state between runs, not worth solving now.

## Report format reference (confirmed real schema from a working fold)
Relevant fields per report-json: main.roi, main.netProfit, main.startingCapital,
main.testingPeriod.formatted, equity.maxEquityDrawdownPercent,
tradeStatistics.profitFactor.all, tradeStatistics.totalTrades.all,
tradeStatistics.winningTrades.all, tradeStatistics.averageTrade.all,
history.items[] (individual closed trades).
