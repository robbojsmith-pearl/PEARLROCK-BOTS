using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using PearlrockBots.SqueezeBreakout.Core;

namespace cAlgo.Robots
{
    // ════════════════════════════════════════════════════════════════════
    // XAUUSD Squeeze Breakout v1
    // Pearlrock Systematic — long/short volatility-compression breakout
    //
    // A trade opens on the bar AFTER a Bollinger-Band-inside-Keltner-Channel
    // squeeze just released, when ALL of these align in one direction:
    //   - Price vs 200H SMA (trend gate)
    //   - LR-projected momentum sign matches trend
    //   - LR-projected momentum is strengthening vs prior bar
    //
    // Stop  = 3×ATR. TP = 6×ATR. Trail = 3×ATR ratchet. Plus momentum-fade soft exit.
    // One position at a time. Risk-based sizing capped by leverage.
    //
    // All strategy logic lives in PearlrockBots.SqueezeBreakout.Core (xUnit-tested).
    // This cBot is a thin orchestrator.
    // ════════════════════════════════════════════════════════════════════
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XAUUSD_SqueezeBreakout_v1 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "XAU_SQZ_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Squeeze ──────────────────────────────────────────────────
        [Parameter("BB Period", DefaultValue = 20, MinValue = 5, Group = "Squeeze")]
        public int BBPeriod { get; set; }
        [Parameter("BB StdDev", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Squeeze")]
        public double BBStdDev { get; set; }
        [Parameter("KC Period", DefaultValue = 20, MinValue = 5, Group = "Squeeze")]
        public int KCPeriod { get; set; }
        [Parameter("KC Multiplier", DefaultValue = 1.5, MinValue = 0.5, Step = 0.1, Group = "Squeeze")]
        public double KCMultiplier { get; set; }

        // ── Trend ────────────────────────────────────────────────────
        [Parameter("Trend SMA Period", DefaultValue = 200, MinValue = 20, Group = "Trend")]
        public int TrendSmaPeriod { get; set; }

        // ── Momentum ─────────────────────────────────────────────────
        [Parameter("Momentum Window", DefaultValue = 20, MinValue = 5, Group = "Momentum")]
        public int MomentumWindow { get; set; }
        [Parameter("Momentum Fade Ratio (0..1)", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 0.95, Step = 0.05, Group = "Momentum")]
        public double MomentumFadeRatio { get; set; }

        // ── ATR / Risk ───────────────────────────────────────────────
        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, Group = "Risk")]
        public int AtrPeriod { get; set; }
        [Parameter("SL ATR Multiple", DefaultValue = 3.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }
        [Parameter("TP ATR Multiple", DefaultValue = 6.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double TpAtrMultiple { get; set; }
        [Parameter("Trail ATR Multiple", DefaultValue = 3.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double TrailAtrMultiple { get; set; }
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }
        [Parameter("Max Leverage", DefaultValue = 10.0, MinValue = 1.0, Step = 0.5, Group = "Risk")]
        public double MaxLeverage { get; set; }
        [Parameter("Leverage Buffer", DefaultValue = 0.2, MinValue = 0.0, Step = 0.05, Group = "Risk")]
        public double LeverageBuffer { get; set; }

        // ── Direction ────────────────────────────────────────────────
        [Parameter("Allow Longs",  DefaultValue = true,  Group = "Direction")]
        public bool AllowLongs { get; set; }
        [Parameter("Allow Shorts", DefaultValue = true,  Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Trade Control ────────────────────────────────────────────
        [Parameter("Trade Start (HH:mm UTC)", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }
        [Parameter("Trade End (HH:mm UTC)", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }
        [Parameter("Max Trade Duration (hours, 0=off)", DefaultValue = 0, MinValue = 0, MaxValue = 240, Group = "Trade Control")]
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
        [Parameter("Max Fitness DD %", DefaultValue = 30.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }
        [Parameter("Backtest Years", DefaultValue = 14.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }
        [Parameter("Fitness PF Cap", DefaultValue = 3.0, MinValue = 1.0, Step = 0.1, Group = "Fitness")]
        public double FitnessPfCap { get; set; }
        [Parameter("Min Average Trade ($)", DefaultValue = 10.0, MinValue = 0, Step = 1, Group = "Fitness")]
        public double MinAverageTradeProfit { get; set; }

        // ── Logging ──────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // ── cAlgo indicators ─────────────────────────────────────────
        private BollingerBands _bb;
        private KeltnerChannels _kc;
        private SimpleMovingAverage _trendSma;
        private AverageTrueRange _atr;
        private SimpleMovingAverage _midlineSma;

        // ── Core engines ─────────────────────────────────────────────
        private SqueezeDetector _squeeze;
        private MomentumProjector _momentum;
        private EntrySignalConfig _entryCfg;
        private RiskSizer _riskSizer;

        // ── Per-trade state ──────────────────────────────────────────
        private AtrTrailingStop _trail;
        private double _takeProfitPrice;
        private double _previousMomentum;
        private bool _hasPreviousMomentum;

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

            _bb = Indicators.BollingerBands(Bars.ClosePrices, BBPeriod, BBStdDev, MovingAverageType.Simple);
            _kc = Indicators.KeltnerChannels(KCPeriod, MovingAverageType.Simple,
                                             KCPeriod, MovingAverageType.Simple,
                                             KCMultiplier);
            _trendSma = Indicators.SimpleMovingAverage(Bars.ClosePrices, TrendSmaPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _midlineSma = Indicators.SimpleMovingAverage(Bars.ClosePrices, MomentumWindow);

            _squeeze = new SqueezeDetector();
            _momentum = new MomentumProjector(MomentumWindow);
            _entryCfg = new EntrySignalConfig
            {
                AllowLongs = AllowLongs,
                AllowShorts = AllowShorts
            };
            _riskSizer = new RiskSizer(new RiskSizerConfig
            {
                RiskPercent = RiskPercent,
                MaxLeverage = MaxLeverage,
                LeverageBuffer = LeverageBuffer
            });

            Positions.Closed += OnPositionClosed;

            Print($"=== {BotLabel} (v3 squeeze breakout) on {SymbolName} H1 ===");
            Print($"BB={BBPeriod}/{BBStdDev:F1}σ  KC={KCPeriod}/{KCMultiplier:F1}  Trend SMA={TrendSmaPeriod}");
            Print($"Mom window={MomentumWindow}  fadeRatio={MomentumFadeRatio}");
            Print($"ATR={AtrPeriod}  SL={SlAtrMultiple}×  TP={TpAtrMultiple}×  Trail={TrailAtrMultiple}×");
            Print($"Risk={RiskPercent}%  MaxLev={MaxLeverage}x  LevBuf={LeverageBuffer}");
            Print($"Allow Longs={AllowLongs}  Shorts={AllowShorts}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Hour) return;
            if (!IndicatorsReady()) return;

            // ── Update Core engines ──────────────────────────────────
            // Use barsAgo=1 (last closed) for the indicator reads.
            double bbUpper = _bb.Top.Last(1);
            double bbLower = _bb.Bottom.Last(1);
            double kcUpper = _kc.Top.Last(1);
            double kcLower = _kc.Bottom.Last(1);
            _squeeze.Update(bbUpper, bbLower, kcUpper, kcLower);

            double price = Bars.ClosePrices.Last(1);
            double midline = MomentumProjector.ComputeMidline(
                HighestHighOverWindow(), LowestLowOverWindow(), _midlineSma.Result.Last(1));
            double momSource = price - midline;
            _momentum.Add(momSource);
            if (!_momentum.IsReady) return;
            double momCurr = _momentum.Project();

            // ── Position management ──────────────────────────────────
            var open = OpenPosition();
            if (open != null)
            {
                ManageOpenPosition(open, price, momCurr);
                _previousMomentum = momCurr;
                _hasPreviousMomentum = true;
                return;
            }

            // ── Gating before entry ──────────────────────────────────
            if (CheckCooldown()) { _previousMomentum = momCurr; _hasPreviousMomentum = true; return; }
            if (!PassesDayFilter()) { _previousMomentum = momCurr; _hasPreviousMomentum = true; return; }
            if (!IsInTradeHours()) { _previousMomentum = momCurr; _hasPreviousMomentum = true; return; }
            if (!SpreadOk()) { _previousMomentum = momCurr; _hasPreviousMomentum = true; return; }

            // Need a previous momentum to decide if it's strengthening.
            if (!_hasPreviousMomentum)
            {
                _previousMomentum = momCurr;
                _hasPreviousMomentum = true;
                return;
            }

            // ── Entry decision ───────────────────────────────────────
            var entryInput = new EntrySignalInput(
                squeezeJustReleased: _squeeze.JustReleased,
                price: price,
                trendSma: _trendSma.Result.Last(1),
                momentumCurrent: momCurr,
                momentumPrevious: _previousMomentum);

            var decision = EntrySignal.Evaluate(entryInput, _entryCfg);
            if (VerboseLogging && _squeeze.JustReleased)
                Log($"SIGNAL release ({decision.Direction}) — {decision.Reason}");

            if (!decision.ShouldEnter)
            {
                _previousMomentum = momCurr;
                _hasPreviousMomentum = true;
                return;
            }

            PlaceEntry(decision.Direction);
            _previousMomentum = momCurr;
            _hasPreviousMomentum = true;
        }

        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPosition(Position pos, double price, double currMomentum)
        {
            // 1. Hard TP
            if ((pos.TradeType == TradeType.Buy  && price >= _takeProfitPrice) ||
                (pos.TradeType == TradeType.Sell && price <= _takeProfitPrice))
            {
                Print($"TP HIT @ {price:F2} | net={pos.NetProfit:F2}");
                ClosePosition(pos);
                return;
            }

            // 2. Trailing stop
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

            // 3. Momentum fade (soft)
            if (_hasPreviousMomentum)
            {
                var side = pos.TradeType == TradeType.Buy ? Side.Long : Side.Short;
                var fadeDecision = MomentumFadeExit.Evaluate(
                    side, currMomentum, _previousMomentum, MomentumFadeRatio);
                if (fadeDecision.ShouldExit)
                {
                    Print($"FADE | {fadeDecision.Reason} | net={pos.NetProfit:F2}");
                    ClosePosition(pos);
                    return;
                }
            }

            // 4. Max trade duration
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
        private void PlaceEntry(Side direction)
        {
            double atrNow = _atr.Result.LastValue;
            if (atrNow <= 0) { Log("ENTRY BLOCKED — atr_not_ready"); return; }

            double stopDist = atrNow * SlAtrMultiple;
            double tpDist = atrNow * TpAtrMultiple;
            double currentPrice = direction == Side.Long ? Symbol.Ask : Symbol.Bid;

            var sizing = new SizingInput(
                equity: Account.Equity,
                stopDistance: stopDist,
                currentPrice: currentPrice,
                minVolume: Symbol.VolumeInUnitsMin,
                maxVolume: Symbol.VolumeInUnitsMax,
                normalizeVolume: u => Symbol.NormalizeVolumeInUnits(u, RoundingMode.Down));

            var (volume, reject) = _riskSizer.Calculate(sizing);
            if (volume == null) { Log($"ENTRY BLOCKED — {reject}"); return; }

            double slPips = stopDist / Symbol.PipSize;
            double tpPips = tpDist / Symbol.PipSize;
            var tradeType = direction == Side.Long ? TradeType.Buy : TradeType.Sell;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume.Value, BotLabel, slPips, tpPips);
            if (!result.IsSuccessful)
            {
                Print($"ORDER FAILED: {result.Error}");
                return;
            }

            // Initialise per-trade state
            double entry = result.Position.EntryPrice;
            _takeProfitPrice = direction == Side.Long ? entry + tpDist : entry - tpDist;
            double initialTrail = direction == Side.Long ? entry - atrNow * TrailAtrMultiple
                                                         : entry + atrNow * TrailAtrMultiple;
            _trail = new AtrTrailingStop(direction, initialTrail, TrailAtrMultiple);

            Print($"[{Server.Time:yyyy-MM-dd HH:mm}] {direction} OPEN @ {entry:F2} | " +
                  $"vol={volume:F2} sl={slPips:F1}p tp={tpPips:F1}p trail={initialTrail:F2}");
        }

        // ─────────────────────────────────────────────────────────────
        private bool IndicatorsReady() =>
            _bb.Top.Count > BBPeriod + 5 &&
            _kc.Top.Count > KCPeriod + 5 &&
            _trendSma.Result.Count > TrendSmaPeriod + 5 &&
            _atr.Result.Count > AtrPeriod + 5 &&
            _midlineSma.Result.Count > MomentumWindow + 5 &&
            Bars.Count > MomentumWindow + 5;

        private double HighestHighOverWindow()
        {
            double max = double.MinValue;
            for (int i = 1; i <= MomentumWindow; i++)
            {
                double h = Bars.HighPrices.Last(i);
                if (h > max) max = h;
            }
            return max;
        }

        private double LowestLowOverWindow()
        {
            double min = double.MaxValue;
            for (int i = 1; i <= MomentumWindow; i++)
            {
                double l = Bars.LowPrices.Last(i);
                if (l < min) min = l;
            }
            return min;
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
        // Custom optimizer fitness (Core/Fitness.cs).
        // Hard-rejects under-trading curve-fits AND strategies whose per-trade
        // edge is too small to survive real spreads.
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
