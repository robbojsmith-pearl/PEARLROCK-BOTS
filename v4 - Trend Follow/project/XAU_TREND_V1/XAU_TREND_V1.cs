using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using PearlrockBots.TrendFollow.Core;

namespace cAlgo.Robots
{
    // ════════════════════════════════════════════════════════════════════
    // XAU_TREND_V1
    // Pearlrock Systematic — long-only 200H trend follower, gated and sized
    // by trend-efficiency instead of a static session/hour filter or GARCH
    // volatility (both tested, efficiency was the one that actually
    // separated the strategy's winning months from its losing ones —
    // r=+0.729 p<0.0001 vs GARCH-vol's r=-0.035 p=0.70, see
    // xau_trend_v1_garch_regime_check.py).
    //
    // A trade opens whenever flat and price is above the 200H SMA, AND
    // trend-efficiency (net move / path length, 200H) clears a gate
    // threshold — keeps the worst chop out entirely rather than trading it.
    // Size scales continuously with efficiency among what survives the
    // gate: cleaner trends get sized up (to a cap), noisier-but-still-
    // qualifying trades get sized down (to a floor) — leans into strength
    // without treating every qualifying entry as equally good.
    //
    // Stop = 2xATR, TP = 5xATR (both broker-side orders), trail = 3.6xATR
    // ratchet (software). Same exit stack as XAU_SQZ_V1 — a dedicated
    // sweep (xau_trend_v1_exit_sweep.py) found no train-selected
    // alternative that held up out-of-sample, so it was left unchanged
    // rather than "optimized" into something that looked better in-sample
    // only. One position at a time.
    //
    // Deliberately long-only — see Core/EntrySignal.cs for why there's no
    // AllowShorts toggle at all, not just a default-off one.
    //
    // Selected via train (2013-2016, gold's bear market) / test (2017-2026,
    // spanning bear recovery, COVID, a real 2021-2022 losing stretch, and
    // the 2023-2026 bull) — never re-fit on the test window. See
    // xau_trend_v1_full_history_wf.py, xau_trend_v1_combined.py, and
    // PEARLROCK-BOTS-REPO session notes for the full validation trail.
    //
    // All strategy logic lives in PearlrockBots.TrendFollow.Core.
    // This cBot is a thin orchestrator.
    // ════════════════════════════════════════════════════════════════════
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XAU_TREND_V1 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "XAU_TREND_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Trend ────────────────────────────────────────────────────
        [Parameter("Trend SMA Period", DefaultValue = 200, MinValue = 20, Group = "Trend")]
        public int TrendSmaPeriod { get; set; }

        // ── Efficiency (regime gate + sizing) ───────────────────────
        [Parameter("Efficiency Window", DefaultValue = 200, MinValue = 20, Group = "Efficiency")]
        public int EfficiencyWindow { get; set; }
        [Parameter("Efficiency Gate Threshold", DefaultValue = 0.03, MinValue = 0.0, MaxValue = 1.0, Step = 0.01, Group = "Efficiency")]
        public double EfficiencyGateThreshold { get; set; }
        [Parameter("Reference Efficiency", DefaultValue = 0.0782, MinValue = 0.001, Step = 0.001, Group = "Efficiency")]
        public double ReferenceEfficiency { get; set; }
        [Parameter("Min Sizing Factor", DefaultValue = 0.25, MinValue = 0.01, Step = 0.05, Group = "Efficiency")]
        public double MinSizingFactor { get; set; }
        [Parameter("Max Sizing Factor", DefaultValue = 2.0, MinValue = 1.0, Step = 0.1, Group = "Efficiency")]
        public double MaxSizingFactor { get; set; }

        // ── ATR / Risk ───────────────────────────────────────────────
        [Parameter("ATR Period", DefaultValue = 20, MinValue = 2, Group = "Risk")]
        public int AtrPeriod { get; set; }
        [Parameter("SL ATR Multiple", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }
        [Parameter("TP ATR Multiple", DefaultValue = 5.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double TpAtrMultiple { get; set; }
        [Parameter("Trail ATR Multiple", DefaultValue = 3.6, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double TrailAtrMultiple { get; set; }
        [Parameter("Risk % per Trade", DefaultValue = 1.0, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }
        [Parameter("Max Leverage", DefaultValue = 10.0, MinValue = 1.0, Step = 0.5, Group = "Risk")]
        public double MaxLeverage { get; set; }
        [Parameter("Leverage Buffer", DefaultValue = 0.2, MinValue = 0.0, Step = 0.05, Group = "Risk")]
        public double LeverageBuffer { get; set; }

        // ── Trade Control ────────────────────────────────────────────
        [Parameter("Trade Start (HH:mm UTC)", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }
        [Parameter("Trade End (HH:mm UTC)", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }
        [Parameter("Max Trade Duration (hours, 0=off)", DefaultValue = 0, MinValue = 0, MaxValue = 2400, Group = "Trade Control")]
        public int MaxTradeHours { get; set; }
        [Parameter("Cooldown Minutes After Exit", DefaultValue = 0, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // ── Day Filter ───────────────────────────────────────────────
        [Parameter("Use Day Filter", DefaultValue = false, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }
        [Parameter("Trade Monday",    DefaultValue = true, Group = "Day Filter")] public bool TradeMonday    { get; set; }
        [Parameter("Trade Tuesday",   DefaultValue = true, Group = "Day Filter")] public bool TradeTuesday   { get; set; }
        [Parameter("Trade Wednesday", DefaultValue = true, Group = "Day Filter")] public bool TradeWednesday { get; set; }
        [Parameter("Trade Thursday",  DefaultValue = true, Group = "Day Filter")] public bool TradeThursday  { get; set; }
        [Parameter("Trade Friday",    DefaultValue = true, Group = "Day Filter")] public bool TradeFriday    { get; set; }

        // ── Safety ───────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }
        [Parameter("Max Spread", DefaultValue = 0.5, Step = 0.05, Group = "Safety")]
        public double MaxSpread { get; set; }

        // ── Fitness ──────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 40, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }
        [Parameter("Min Trades Per Year", DefaultValue = 6.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }
        [Parameter("Max Fitness DD %", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }
        [Parameter("Backtest Years", DefaultValue = 13.7, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }
        [Parameter("Fitness PF Cap", DefaultValue = 3.0, MinValue = 1.0, Step = 0.1, Group = "Fitness")]
        public double FitnessPfCap { get; set; }
        [Parameter("Min Average Trade ($)", DefaultValue = 10.0, MinValue = 0, Step = 1, Group = "Fitness")]
        public double MinAverageTradeProfit { get; set; }

        // ── Logging ──────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // ── cAlgo indicators ─────────────────────────────────────────
        private SimpleMovingAverage _trendSma;
        private AverageTrueRange _atr;

        // ── Core engines ─────────────────────────────────────────────
        private RiskSizer _riskSizer;

        // ── Per-trade state ──────────────────────────────────────────
        private AtrTrailingStop _trail;
        private double _takeProfitPrice;

        // ── Exit tracking ────────────────────────────────────────────
        private DateTime _lastExitTime = DateTime.MinValue;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Hour)
            {
                Print($"ABORT: Must run on H1. Current = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _trendSma = Indicators.SimpleMovingAverage(Bars.ClosePrices, TrendSmaPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);

            _riskSizer = new RiskSizer(new RiskSizerConfig
            {
                RiskPercent = RiskPercent,
                MaxLeverage = MaxLeverage,
                LeverageBuffer = LeverageBuffer
            });

            Positions.Closed += OnPositionClosed;

            Print($"=== {BotLabel} (long-only trend + efficiency) on {SymbolName} H1 ===");
            Print($"Trend SMA={TrendSmaPeriod}  Efficiency window={EfficiencyWindow} gate={EfficiencyGateThreshold} "
                  + $"refEff={ReferenceEfficiency} sizingFactor=[{MinSizingFactor},{MaxSizingFactor}]");
            Print($"ATR={AtrPeriod}  SL={SlAtrMultiple}x  TP={TpAtrMultiple}x  Trail={TrailAtrMultiple}x");
            Print($"Risk={RiskPercent}%  MaxLev={MaxLeverage}x  LevBuf={LeverageBuffer}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Hour) return;
            if (!IndicatorsReady()) return;

            double price = Bars.ClosePrices.Last(1);
            double trendSma = _trendSma.Result.Last(1);
            double efficiency = Efficiency.Compute(GetRecentCloses(EfficiencyWindow + 1));

            // ── Position management ──────────────────────────────────
            var open = OpenPosition();
            if (open != null)
            {
                ManageOpenPosition(open, price);
                return;
            }

            // ── Gating before entry ──────────────────────────────────
            if (CheckCooldown()) return;
            if (!PassesDayFilter()) return;
            if (!IsInTradeHours()) return;
            if (!SpreadOk()) return;

            // ── Entry decision ───────────────────────────────────────
            var decision = EntrySignal.Evaluate(price, trendSma, efficiency, EfficiencyGateThreshold);
            if (VerboseLogging && !decision.ShouldEnter)
                Log($"NO ENTRY — {decision.Reason}");

            if (!decision.ShouldEnter) return;

            PlaceEntry(efficiency);
        }

        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPosition(Position pos, double price)
        {
            // 1. Hard TP (also a broker-side order — this is a belt-and-braces
            //    software check, same pattern as XAU_SQZ_V1)
            if (price >= _takeProfitPrice)
            {
                Print($"TP HIT @ {price:F2} | net={pos.NetProfit:F2}");
                ClosePosition(pos);
                return;
            }

            // 2. Trailing stop (software-only, live ATR)
            double atrNow = _atr.Result.LastValue;
            if (_trail != null && atrNow > 0)
            {
                var trailDecision = _trail.Update(price, atrNow);
                if (trailDecision.ShouldExit)
                {
                    Print($"TRAIL @ {price:F2} | {trailDecision.Reason} | net={pos.NetProfit:F2}");
                    ClosePosition(pos);
                    return;
                }
            }

            // 3. Max trade duration
            if (MaxTradeHours > 0)
            {
                var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
                if (hoursOpen >= MaxTradeHours)
                {
                    Print($"MAX DURATION EXIT | {hoursOpen:F1}h | net={pos.NetProfit:F2}");
                    ClosePosition(pos);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        private void PlaceEntry(double efficiency)
        {
            double atrNow = _atr.Result.LastValue;
            if (atrNow <= 0) { Log("ENTRY BLOCKED — atr_not_ready"); return; }

            double stopDist = atrNow * SlAtrMultiple;
            double tpDist = atrNow * TpAtrMultiple;
            double currentPrice = Symbol.Ask;

            double sizingFactor = Efficiency.SizingFactor(efficiency, ReferenceEfficiency, MinSizingFactor, MaxSizingFactor);

            var sizing = new SizingInput(
                equity: Account.Equity,
                stopDistance: stopDist,
                currentPrice: currentPrice,
                minVolume: Symbol.VolumeInUnitsMin,
                maxVolume: Symbol.VolumeInUnitsMax,
                sizingFactor: sizingFactor,
                normalizeVolume: u => Symbol.NormalizeVolumeInUnits(u, RoundingMode.Down));

            var (volume, reject) = _riskSizer.Calculate(sizing);
            if (volume == null) { Log($"ENTRY BLOCKED — {reject}"); return; }

            double slPips = stopDist / Symbol.PipSize;
            double tpPips = tpDist / Symbol.PipSize;

            var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume.Value, BotLabel, slPips, tpPips);
            if (!result.IsSuccessful)
            {
                Print($"ORDER FAILED: {result.Error}");
                return;
            }

            double entry = result.Position.EntryPrice;
            _takeProfitPrice = entry + tpDist;
            double initialTrail = entry - atrNow * TrailAtrMultiple;
            _trail = new AtrTrailingStop(Side.Long, initialTrail, TrailAtrMultiple);

            Print($"[{Server.Time:yyyy-MM-dd HH:mm}] LONG OPEN @ {entry:F2} | " +
                  $"vol={volume:F2} sizingFactor={sizingFactor:F2} eff={efficiency:F4} sl={slPips:F1}p tp={tpPips:F1}p trail={initialTrail:F2}");
        }

        // ─────────────────────────────────────────────────────────────
        private bool IndicatorsReady() =>
            _trendSma.Result.Count > TrendSmaPeriod + 5 &&
            _atr.Result.Count > AtrPeriod + 5 &&
            Bars.Count > EfficiencyWindow + 5;

        private double[] GetRecentCloses(int windowPlusOne)
        {
            var arr = new double[windowPlusOne];
            for (int j = 0; j < windowPlusOne; j++)
                arr[j] = Bars.ClosePrices.Last(windowPlusOne - j);
            return arr;
        }

        private Position OpenPosition() =>
            Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private bool CheckCooldown() =>
            CooldownMinutes > 0 && _lastExitTime != DateTime.MinValue &&
            Server.Time < _lastExitTime.AddMinutes(CooldownMinutes);

        private bool PassesDayFilter()
        {
            if (!UseDayFilter) return true;
            switch (Server.Time.DayOfWeek)
            {
                case DayOfWeek.Monday:    return TradeMonday;
                case DayOfWeek.Tuesday:   return TradeTuesday;
                case DayOfWeek.Wednesday: return TradeWednesday;
                case DayOfWeek.Thursday:  return TradeThursday;
                case DayOfWeek.Friday:    return TradeFriday;
                default:                  return false;
            }
        }

        private bool IsInTradeHours()
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd))
                return true;
            if (!TimeSpan.TryParse(TradeStart, out var s) || !TimeSpan.TryParse(TradeEnd, out var e))
                return true;
            var t = Server.Time.TimeOfDay;
            if (s <= e) return t >= s && t < e;
            return t >= s || t < e;
        }

        private bool SpreadOk() => !UseSpreadFilter || (Symbol.Ask - Symbol.Bid) <= MaxSpread;

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName || args.Position.Label != BotLabel) return;
            _lastExitTime = Server.Time;
            _trail = null;
            _takeProfitPrice = 0;
        }

        private void Log(string msg) { if (VerboseLogging) Print(msg); }

        // ─────────────────────────────────────────────────────────────
        protected override double GetFitness(GetFitnessArgs args)
        {
            return Fitness.Compute(
                new FitnessInput(
                    totalTrades:          args.TotalTrades,
                    winningTrades:        args.WinningTrades,
                    netProfit:            args.NetProfit,
                    maxEquityDrawdownPct: args.MaxEquityDrawdownPercentages,
                    profitFactor:         args.ProfitFactor,
                    backtestYears:        BacktestYears),
                new FitnessConfig
                {
                    MinTotalTrades        = MinTotalTrades,
                    MinTradesPerYear      = MinTradesPerYear,
                    MaxDrawdownPct        = MaxFitnessDrawdownPct,
                    PfCap                 = FitnessPfCap,
                    MinAverageTradeProfit = MinAverageTradeProfit
                });
        }
    }
}
